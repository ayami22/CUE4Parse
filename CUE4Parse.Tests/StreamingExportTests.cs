using System.Runtime.CompilerServices;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Exporters;
using CUE4Parse_Conversion.Options;
using static CUE4Parse.Tests.Fixtures.FixtureTestUtilities;
using CUE4Parse.Tests.Fixtures;

namespace CUE4Parse.Tests;

public sealed class StreamingExportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cue4parse-stream-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task WritesBeforeScannerContinuesAndDrainsDependencies()
    {
        var session = new ExportSession();
        var summary = await session.RunStreamingAsync(_directory, new(), _ =>
        {
            session.Add(new ProbeExporter("Parent", () => session.Add(new ProbeExporter("Child"))));
            Assert.True(File.Exists(Path.Combine(_directory, "Parent.bin")));
            Assert.True(File.Exists(Path.Combine(_directory, "Child.bin")));
            Assert.Equal(0, session.TotalQueued);
            session.Add(new ProbeExporter("Next"));
        });
        Assert.Equal(new ExportSummary(3, 0), summary);
        Assert.False(session.IsRunning);
    }

    [Fact]
    public async Task SharedDependenciesAreDeduplicatedPerDestination()
    {
        var session = new ExportSession();
        var summary = await session.RunStreamingAsync(_directory, new(), _ =>
        {
            session.Add(new ProbeExporter("Shared") { OutputFolderOverride = "ModelA" });
            session.Add(new ProbeExporter("Shared") { OutputFolderOverride = "ModelA" });
            session.Add(new ProbeExporter("Shared") { OutputFolderOverride = "ModelB" });
        });
        Assert.Equal(2, summary.Succeeded);
        Assert.True(File.Exists(Path.Combine(_directory, "ModelA", "Shared.bin")));
        Assert.True(File.Exists(Path.Combine(_directory, "ModelB", "Shared.bin")));
    }

    [Fact]
    public async Task RecursiveDependenciesDoNotDeadlockOrExportTwice()
    {
        var session = new ExportSession();
        var summary = await session.RunStreamingAsync(_directory, new(), _ =>
            session.Add(new ProbeExporter("A", () => session.Add(new ProbeExporter("B", () => session.Add(new ProbeExporter("A")))))))
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, summary.Succeeded);
    }

    [Fact]
    public async Task CancellationStopsScannerAndAllowsSessionReuse()
    {
        var session = new ExportSession();
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.RunStreamingAsync(_directory, new(), ct =>
        {
            session.Add(new ProbeExporter("First"));
            cts.Cancel();
            session.Add(new ProbeExporter("NeverWritten"));
        }, ct: cts.Token));
        Assert.False(session.IsRunning);
        Assert.Equal(0, session.TotalQueued);
        Assert.False(File.Exists(Path.Combine(_directory, "NeverWritten.bin")));
        var result = await session.RunStreamingAsync(_directory, new(), _ => session.Add(new ProbeExporter("First")));
        Assert.Equal(1, result.Succeeded);
    }

    [Fact]
    public async Task CancellationInsideDependencyUnwindsAllExporters()
    {
        var session = new ExportSession();
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.RunStreamingAsync(_directory, new(), _ =>
            session.Add(new ProbeExporter("Parent", () =>
                session.Add(new ProbeExporter("Child", () => { cts.Cancel(); cts.Token.ThrowIfCancellationRequested(); })))), ct: cts.Token));
        Assert.False(session.IsRunning);
        Assert.Equal(0, session.TotalQueued);
    }

    [Fact]
    public async Task FailureIsCountedAndNextAssetStillExports()
    {
        var session = new ExportSession();
        var summary = await session.RunStreamingAsync(_directory, new(), _ =>
        {
            session.Add(new ProbeExporter("Bad", () => throw new InvalidDataException("test failure")));
            session.Add(new ProbeExporter("Good"));
        });
        Assert.Equal(new ExportSummary(1, 1), summary);
        Assert.True(File.Exists(Path.Combine(_directory, "Good.bin")));
    }

    [Fact]
    public async Task ScanFailuresAreIncludedInSummary()
    {
        var session = new ExportSession();
        var summary = await session.RunStreamingAsync(_directory, new(), _ =>
        {
            session.ReportScanFailure("Broken.uasset", new InvalidDataException("Invalid package"));
            session.Add(new ProbeExporter("Good"));
        });
        Assert.Equal(new ExportSummary(1, 1), summary);
    }

    [Fact]
    public async Task ScannerFailureAndInvalidDirectoryDoNotLeaveSessionRunning()
    {
        var session = new ExportSession();
        await Assert.ThrowsAsync<InvalidDataException>(() => session.RunStreamingAsync(_directory, new(), _ => throw new InvalidDataException()));
        Assert.False(session.IsRunning);
        await Assert.ThrowsAsync<ArgumentException>(() => session.RunStreamingAsync("", new(), _ => { }));
        Assert.False(session.IsRunning);
    }

    [Fact]
    public async Task ExistingQueueAndRemovedItemsAreHandled()
    {
        var session = new ExportSession();
        var removed = new ProbeExporter("Removed");
        session.Add(removed);
        session.Remove(removed.ObjectPath);
        session.Add(new ProbeExporter("Queued"));
        var summary = await session.RunStreamingAsync(_directory, new(), _ =>
        {
            Assert.True(File.Exists(Path.Combine(_directory, "Queued.bin")));
            session.Add(new ProbeExporter("Scanned"));
        });
        Assert.Equal(2, summary.Succeeded);
        Assert.False(File.Exists(Path.Combine(_directory, "Removed.bin")));
    }

    [Fact]
    public async Task ConcurrentRunAndClearAreRejected()
    {
        var session = new ExportSession();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var run = session.RunStreamingAsync(_directory, new(), _ => { started.SetResult(); release.Wait(TimeSpan.FromSeconds(10)); });
        await started.Task;
        try
        {
            Assert.Throws<InvalidOperationException>(session.Clear);
            Assert.Throws<InvalidOperationException>(() => session.Add(new ProbeExporter("External")));
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.RunStreamingAsync(_directory, new(), _ => { }));
        }
        finally { release.Set(); }
        await run;
    }

    [Fact]
    public async Task CompletedPayloadsAreCollectibleWhileScanIsStillRunning()
    {
        var session = new ExportSession();
        var refs = new List<WeakReference>();
        var summary = await session.RunStreamingAsync(_directory, new(), _ =>
        {
            // 1 GiB of cumulative decoded payloads, with only tiny test files written.
            for (var i = 0; i < 512; i++)
            {
                refs.Add(AddLargeExporter(session, i));
                if (i % 32 != 31) continue;
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Assert.DoesNotContain(refs, reference => reference.IsAlive);
                Assert.Equal(0, session.TotalQueued);
            }
        });
        Assert.Equal(512, summary.Succeeded);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AddLargeExporter(ExportSession session, int i)
    {
        var exporter = new ProbeExporter("Large" + i, payloadBytes: 2 * 1024 * 1024);
        var reference = new WeakReference(exporter);
        session.Add(exporter);
        return reference;
    }

    [Theory]
    [InlineData(FixtureSerialization.Tagged, EMeshFormat.ActorX)]
    [InlineData(FixtureSerialization.Unversioned, EMeshFormat.ActorX)]
    [InlineData(FixtureSerialization.Tagged, EMeshFormat.UEFormat)]
    [InlineData(FixtureSerialization.Unversioned, EMeshFormat.UEFormat)]
    [InlineData(FixtureSerialization.Tagged, EMeshFormat.Gltf2)]
    [InlineData(FixtureSerialization.Tagged, EMeshFormat.USD)]
    public async Task FixtureMeshOutputMatchesQueuedExport(FixtureSerialization serialization, EMeshFormat format)
    {
        using var provider = CreateMountedIoStoreProvider(serialization, compression: FixtureCompression.Uncompressed);
        const string path = "CUE4ParseFixtures/Content/Fixtures/Meshes/SM_Fixture.uasset";
        var options = new ExportOptions(meshFormat: format, meshQuality: EMeshQuality.All, exportMaterials: true);
        var queued = new ExportSession();
        queued.Add(LoadExport<UStaticMesh>(provider, path, "SM_Fixture"));
        var results = await queued.RunAsync(Path.Combine(_directory, "queued"), options);
        Assert.All(results, result => Assert.True(result.Success, result.Error?.ToString()));
        var streamed = new ExportSession();
        var summary = await streamed.RunStreamingAsync(Path.Combine(_directory, "streamed"), options, _ =>
            streamed.Add(LoadExport<UStaticMesh>(provider, path, "SM_Fixture")));
        Assert.Equal(new ExportSummary(results.Count, 0), summary);
        var files = Directory.GetFiles(Path.Combine(_directory, "queued"), "*", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(Path.Combine(_directory, "queued"), file);
            Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(_directory, "streamed", relative)));
        }
    }

    [Theory]
    [InlineData(FixtureSerialization.Tagged)]
    [InlineData(FixtureSerialization.Unversioned)]
    public async Task AnimationIndexIsReusedWithinSessionAndReleasedAfterwards(FixtureSerialization serialization)
    {
        using var provider = CreateMountedIoStoreProvider(serialization, compression: FixtureCompression.Uncompressed);
        var session = new ExportSession();
        AnimationExportIndex? previous = null;
        await session.RunStreamingAsync(_directory, new(), ct =>
        {
            var skeleton = LoadExport<USkeleton>(provider,
                "CUE4ParseFixtures/Content/Fixtures/Animations/SKEL_Fixture.uasset", "SKEL_Fixture");
            var index = session.GetAnimationIndex(provider, ct);
            previous = index;
            Assert.Same(index, session.GetAnimationIndex(provider, ct));
            Assert.Contains(index.Find(skeleton.GetPathName(), false), entry => entry.ObjectPath.EndsWith(".AS_Fixture"));
            Assert.Contains(index.Find(skeleton.GetPathName(), false), entry => entry.IsMontage);
            Assert.DoesNotContain(index.Find(skeleton.GetPathName(), true), entry => entry.IsMontage);
            Assert.False(index.Contains("MissingSkeleton", false));
        });
        await session.RunStreamingAsync(_directory, new(), ct =>
            Assert.NotSame(previous, session.GetAnimationIndex(provider, ct)));
    }

    [Theory]
    [InlineData(EMeshFormat.ActorX)]
    [InlineData(EMeshFormat.UEFormat)]
    public async Task UnsupportedFixtureAnimationsAreReportedWithoutLosingMeshOutput(EMeshFormat format)
    {
        using var provider = CreateMountedIoStoreProvider(FixtureSerialization.Tagged, compression: FixtureCompression.Uncompressed);
        var options = new ExportOptions(meshFormat: format, exportMaterials: false, exportAnimations: true,
            exportFolderMode: EExportFolderMode.BySkeleton);
        var queued = new ExportSession();
        queued.Add(LoadExport<USkeletalMesh>(provider,
            "CUE4ParseFixtures/Content/Fixtures/Meshes/SK_Fixture.uasset", "SK_Fixture"));
        var baseline = await queued.RunAsync(Path.Combine(_directory, "queued"), options);
        var baselineFailures = baseline.Where(r => !r.Success).Select(r => (r.ObjectPath, r.Error?.Message)).OrderBy(x => x.ObjectPath).ToArray();
        // These deterministic fixtures exercise indexing but have no supported compressed animation data.
        Assert.NotEmpty(baselineFailures);

        var session = new ExportSession();
        var failures = new List<(string ObjectPath, string? Message)>();
        var streamingDirectory = Path.Combine(_directory, "streamed");
        var summary = await session.RunStreamingAsync(streamingDirectory, options, _ =>
            session.Add(LoadExport<USkeletalMesh>(provider,
                "CUE4ParseFixtures/Content/Fixtures/Meshes/SK_Fixture.uasset", "SK_Fixture")),
            new ImmediateProgress(p =>
            {
                if (p.LastResult is not { Success: false } failure) return;
                // The mesh is already on disk when animation conversion is attempted.
                Assert.Contains(Directory.GetFiles(streamingDirectory, "*", SearchOption.AllDirectories),
                    file => Path.GetFileName(file).StartsWith("SK_Fixture"));
                failures.Add((failure.ObjectPath, failure.Error?.Message));
            }));
        Assert.Equal(baseline.Count(r => r.Success), summary.Succeeded);
        Assert.Equal(baselineFailures.Length, summary.Failed);
        Assert.Equal(baselineFailures, failures.OrderBy(x => x.ObjectPath).ToArray());
    }

    private sealed class ImmediateProgress(Action<ExportProgress> report) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value) => report(value);
    }

    private sealed class ProbeExporter : ExporterBase
    {
        private readonly Action? _onBuild;
        private readonly byte[] _decodedPayload;
        public ProbeExporter(string name, Action? onBuild = null, int payloadBytes = 1) : base(new UObject { Name = name })
        {
            _onBuild = onBuild;
            _decodedPayload = new byte[payloadBytes];
        }
        protected override IReadOnlyList<ExportFile> BuildExportFiles(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _onBuild?.Invoke();
            return [new ExportFile("bin", [_decodedPayload[0], 42])];
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
