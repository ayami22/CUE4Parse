using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Actor;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Component.Landscape;
using CUE4Parse.UE4.Assets.Exports.Component.SplineMesh;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Exports.GeometryCollection;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Rig;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.Engine.Animation;
using CUE4Parse_Conversion.Exporters;
using CUE4Parse_Conversion.Options;

namespace CUE4Parse_Conversion;

public sealed class ExportSession(Action<StreamingLevelFilterArgs, CancellationToken>? streamingLevelFilter = null) : INotifyPropertyChanged
{
    internal readonly Action<StreamingLevelFilterArgs, CancellationToken>? _streamingLevelFilter = streamingLevelFilter;
    internal readonly SemaphoreSlim _streamingLevelFilterLock = new(1, 1);

    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;

    private DirectoryInfo? _baseDirectory;
    internal DirectoryInfo BaseDirectory => _baseDirectory ?? throw new InvalidOperationException("Session is not currently running.");

    private ExportOptions? _options;
    internal ExportOptions Options => _options ?? throw new InvalidOperationException("Session is not currently running.");

    private int _totalQueued;
    public int TotalQueued => Volatile.Read(ref _totalQueued);
    public bool HasQueuedItems => TotalQueued > 0;

    private int _running;
    public bool IsRunning => Volatile.Read(ref _running) == 1;

    private readonly ConcurrentQueue<IExporter> _roots = new();
    private readonly ConcurrentDictionary<string, byte> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly AsyncLocal<bool> _streamingOwner = new();
    private Action<ExporterBase>? _streamExport;
    private Action<ExportResult>? _streamReport;
    private CancellationToken _cancellationToken;
    internal CancellationToken CancellationToken => _cancellationToken;
    private readonly ConcurrentDictionary<IFileProvider, Lazy<AnimationExportIndex>> _animationIndexes = new();

    public AnimationExportIndex GetAnimationIndex(IFileProvider provider, CancellationToken ct) =>
        _animationIndexes.GetOrAdd(provider, p => new Lazy<AnimationExportIndex>(() => AnimationExportIndex.Build(p, ct))).Value;

    public void ReportScanFailure(string path, Exception exception)
    {
        if (!_streamingOwner.Value || _streamReport == null)
            throw new InvalidOperationException("Scan failures can only be reported by the streaming scanner.");
        _cancellationToken.ThrowIfCancellationRequested();
        Serilog.Log.ForContext("ExporterV2", true).ForContext("ClassName", "Package")
            .ForContext("ObjectPath", path).Error(exception, "Failed to scan package for export");
        _streamReport(ExportResult.Failure(path, exception));
    }

    public ExportSession Add(UObject export)
    {
        return export switch
        {
            UTexture texture => Add(new TextureExporter(texture)),
            UMaterialInterface material => Add(new MaterialExporter(material)),
            USkinnedAsset skinnedAsset => Add(new SkinnedAssetExporter(skinnedAsset)),
            UStaticMesh staticMesh => Add(new StaticMeshExporter(staticMesh)),
            UGeometryCollection geometryCollection => Add(new GeometryCollectionExporter(geometryCollection)),
            USkeleton skeleton => Add(new SkeletonExporter(skeleton)),
            UPoseAsset poseAsset => Add(new PoseAssetExporter(poseAsset)),
            UAnimationAsset animation => Add(new AnimationExporter(animation)),
            UDNAAsset dna => Add(new DnaExporter(dna)),
            UWorld world => Add(new WorldExporter(world)),
            ALandscapeProxy landscape => Add(new LandscapeMeshExporter(landscape)),
            ULandscapeComponent landscape => Add(new LandscapeMeshExporter2(landscape)),
            USplineMeshComponent spline => Add(new SplineMeshExporter(spline)),
            _ => throw new NotSupportedException($"Could not create exporter for export of type '{export.GetType().Name}'.")
        };
    }

    public ExportSession Add(ExporterBase exporter)
    {
        if (_streamExport is { } streamExport)
        {
            if (!_streamingOwner.Value)
                throw new InvalidOperationException("Cannot add assets from another operation during streaming export.");
            _cancellationToken.ThrowIfCancellationRequested();
            // Include the destination: shared animations/materials must be written into each model folder.
            if (!_paths.TryAdd(exporter.ObjectPath + "\0" + exporter.OutputFolderOverride, 0)) return this;
            exporter._session = this;
            streamExport(exporter);
            return this;
        }
        // TODO: this prevents 2 exporters messing with the same file from being enqueued in the same run (e.g. MeshExporter / RawDataExporter)
        if (!_paths.TryAdd(exporter.ObjectPath, 0)) return this;

        exporter._session = this;
        _roots.Enqueue(exporter);

        Interlocked.Increment(ref _totalQueued);
        OnPropertyChanged(nameof(TotalQueued));
        OnPropertyChanged(nameof(HasQueuedItems));
        exporter.Log.Debug("Queued for export");
        return this;
    }

    public bool Remove(string objectPath)
    {
        // the exporter stays in _roots but we won't process it, it's fine because nothing actually relies on _roots.Count
        if (IsRunning || !_paths.TryRemove(objectPath, out _))
            return false;

        Interlocked.Decrement(ref _totalQueued);
        OnPropertyChanged(nameof(TotalQueued));
        OnPropertyChanged(nameof(HasQueuedItems));
        return true;
    }

    public void Clear()
    {
        if (IsRunning) throw new InvalidOperationException("Cancel the export before clearing the session.");
        ClearCore();
    }

    private void ClearCore()
    {
        _roots.Clear();
        _paths.Clear();
        _animationIndexes.Clear();
        Interlocked.Exchange(ref _totalQueued, 0);
        OnPropertyChanged(nameof(TotalQueued));
        OnPropertyChanged(nameof(HasQueuedItems));
    }

    /// <summary>
    /// Runs the scanner on a worker. Add applies backpressure by completing an asset and its dependencies
    /// before returning to the scanner. Only the current package/dependency chain is held, not a game-sized
    /// queue. Results are reported immediately, never retained by the session.
    /// </summary>
    public async Task<ExportSummary> RunStreamingAsync(string baseDirectory, ExportOptions options,
        Action<CancellationToken> scan, IProgress<ExportProgress>? progress = null, CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new InvalidOperationException("Session is already running.");

        var completed = 0;
        var succeeded = 0;
        var failed = 0;
        try
        {
            _baseDirectory = new DirectoryInfo(baseDirectory);
            _options = options;
            _cancellationToken = ct;
            _streamReport = result =>
            {
                completed++;
                if (result.Success) succeeded++; else failed++;
                progress?.Report(new ExportProgress(completed, 0, result));
            };
            _streamExport = exporter =>
            {
                ct.ThrowIfCancellationRequested();
                Interlocked.Increment(ref _totalQueued);
                OnPropertyChanged(nameof(TotalQueued));
                try
                {
                    var result = exporter.ExportAsync(ct).GetAwaiter().GetResult();
                    _streamReport(result);
                }
                finally
                {
                    exporter._session = null;
                    Interlocked.Decrement(ref _totalQueued);
                    OnPropertyChanged(nameof(TotalQueued));
                }
            };
            OnPropertyChanged(nameof(IsRunning));
            await Task.Run(() =>
            {
                _streamingOwner.Value = true;
                try
                {
                    // Finish any manually queued work first, releasing each exporter immediately.
                    while (_roots.TryDequeue(out var item))
                    {
                        if (!_paths.TryRemove(item.ObjectPath, out _)) continue;
                        Interlocked.Decrement(ref _totalQueued);
                        Add((ExporterBase)item);
                    }
                    scan(ct);
                    ct.ThrowIfCancellationRequested();
                }
                finally { _streamingOwner.Value = false; }
            }, ct).ConfigureAwait(false);
            progress?.Report(new ExportProgress(completed, completed));
            return new ExportSummary(succeeded, failed);
        }
        finally
        {
            _streamExport = null;
            _streamReport = null;
            ClearCore();
            _options = null;
            _baseDirectory = null;
            _cancellationToken = default;
            Interlocked.Exchange(ref _running, 0);
            OnPropertyChanged(nameof(IsRunning));
        }
    }

    public async Task<IReadOnlyList<ExportResult>> RunAsync(string baseDirectory, ExportOptions options, IProgress<ExportProgress>? progress = null, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
            throw new InvalidOperationException("Session is already running.");

        OnPropertyChanged(nameof(IsRunning));
        _baseDirectory = new DirectoryInfo(baseDirectory);
        _options = options;
        _cancellationToken = ct;

        var results = new ConcurrentQueue<ExportResult>();
        try
        {
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = MaxDegreeOfParallelism, CancellationToken = ct };
            var current = new List<IExporter>();
            while (true)
            {
                current.Clear();
                while (_roots.TryDequeue(out var exporter))
                {
                    ct.ThrowIfCancellationRequested();
                    if (_paths.ContainsKey(exporter.ObjectPath)) // false = exporter was added but then manually removed
                        current.Add(exporter);
                }
                if (current.Count == 0) break;

                await Parallel.ForEachAsync(current, parallelOptions, Process).ConfigureAwait(false);
            }
        }
        finally // just in case cancellation is requested, we still need to clear things up
        {
            var stillQueued = TotalQueued;
            var count = results.Count;

            ClearCore();
            progress?.Report(new ExportProgress(count, count + stillQueued)); // this ensure the last progress reports the actual numbers

            _options = null;
            _baseDirectory = null;
            _cancellationToken = default;
            Interlocked.Exchange(ref _running, 0);
            OnPropertyChanged(nameof(IsRunning));
        }

        return [.. results];

        async ValueTask Process(IExporter exporter, CancellationToken token)
        {
            var result = await exporter.ExportAsync(token).ConfigureAwait(false);
            results.Enqueue(result);

            var stillQueued = Interlocked.Decrement(ref _totalQueued);
            var count = results.Count;
            OnPropertyChanged(nameof(TotalQueued));
            OnPropertyChanged(nameof(HasQueuedItems));

            progress?.Report(new ExportProgress(count, count + stillQueued, result));

        }
    }

    internal string ResolveOutputPath(string savePath, string ext, string? nameSuffix = null)
    {
        var fullPath = Path.Combine(BaseDirectory.FullName, savePath) + nameSuffix + '.' + ext.ToLower();
        var dir = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException($"Cannot determine directory for path: {fullPath}");
        Directory.CreateDirectory(dir);
        return fullPath.Replace('/', '\\');
    }

    internal string ResolveOutputPathInFolder(string folder, string fileName, string ext, string? nameSuffix = null)
    {
        var fullPath = Path.Combine(BaseDirectory.FullName, folder, fileName) + nameSuffix + '.' + ext.ToLower();
        var dir = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException($"Cannot determine directory for path: {fullPath}");
        Directory.CreateDirectory(dir);
        return fullPath.Replace('/', '\\');
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
