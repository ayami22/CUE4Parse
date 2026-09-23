using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Objects.UObject;

namespace CUE4Parse_Conversion;

/// <summary>Session-local animation lookup. Stores paths only; never packages, skeletons or animation data.</summary>
public sealed class AnimationExportIndex
{
    public readonly record struct Entry(string ObjectPath, bool IsMontage);
    private readonly Dictionary<string, List<Entry>> _bySkeleton = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<Entry> Find(string skeletonPath, bool filterMontage) =>
        _bySkeleton.TryGetValue(skeletonPath, out var entries)
            ? entries.Where(e => !filterMontage || !e.IsMontage)
            : [];

    public bool Contains(string skeletonPath, bool filterMontage) => Find(skeletonPath, filterMontage).Any();

    internal static AnimationExportIndex Build(IFileProvider provider, CancellationToken ct)
    {
        var index = new AnimationExportIndex();
        Serilog.Log.Information("Indexing animation paths for this export session");
        foreach (var file in provider.Files.Values)
        {
            ct.ThrowIfCancellationRequested();
            if (!file.IsUePackage) continue;
            // Keep package references scoped to one call so finished packages can be collected.
            IndexPackage(file.Path);
        }
        return index;

        void IndexPackage(string path)
        {
            if (!provider.TryLoadPackage(path, out var package)) return;
            for (var i = 0; i < package.ExportMapLength; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var pointer = new FPackageIndex(package, i + 1).ResolvedObject;
                    if (pointer == null || package is not AbstractUePackage uePackage) continue;
                    // Inspect the class first: do not deserialize meshes/textures while finding animations.
                    if (uePackage.ConstructObject(pointer.Class, package) is not (UAnimSequence or UAnimMontage or UAnimComposite)) continue;
                    if (pointer.Object?.Value is not UAnimationAsset animation ||
                        !animation.Skeleton.TryLoad<USkeleton>(out var skeleton)) continue;
                    var key = skeleton.GetPathName();
                    if (!index._bySkeleton.TryGetValue(key, out var entries))
                        index._bySkeleton[key] = entries = [];
                    entries.Add(new Entry(animation.GetPathName(), animation is UAnimMontage));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Serilog.Log.Debug(ex, "Could not index animation in {PackagePath}", path); }
            }
        }
    }
}
