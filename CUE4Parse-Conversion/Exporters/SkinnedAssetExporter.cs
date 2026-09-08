using System;
using System.Collections.Generic;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Exports.Rig;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse_Conversion.Dto;
using CUE4Parse_Conversion.Formats.Meshes;
using CUE4Parse_Conversion.Options;

namespace CUE4Parse_Conversion.Exporters;

public sealed class SkinnedAssetExporter(USkinnedAsset originalMesh) : MeshExporter<USkinnedAsset>(originalMesh)
{
    protected override IReadOnlyList<ExportFile> BuildFiles(USkinnedAsset originalMesh, IMeshExportFormat format)
    {
        if (Session.Options.ExportMorphTargets)
        {
            originalMesh.PopulateMorphTargetVerticesData();
        }

        using var dto = new SkeletalMeshDto(originalMesh, Session.Options.MeshQuality, Session.Options.NaniteMeshFormat, Session.Options.ExportMorphTargets);
        if (dto.LODs.Count == 0)
        {
            throw new Exception("Skeletal mesh has no LODs");
        }

        if (dto.AssetUserData != null)
        {
            foreach (var userData in dto.AssetUserData)
            {
                if (userData.TryLoad<UDNAAsset>(out var dna))
                {
                    Session.Add(new DnaExporter(dna));
                }
            }
        }

        var materialPaths = EnqueueMaterials(dto.Materials);
        EnqueueMatchingAnimations(originalMesh);
        return format.BuildSkeletalMesh(ObjectName, ObjectPath, Session.Options, dto, materialPaths);
    }

    /// <summary>
    /// "Through Mesh" animation export: scans the provider for animations bound to the same skeleton as this mesh
    /// and queues them into this session (they export in parallel with the mesh). In BySkeleton mode they are grouped
    /// under Have_Skeleton/&lt;SkeletonName&gt;/Animations, otherwise they follow the mesh's own folder.
    /// </summary>
    private void EnqueueMatchingAnimations(USkinnedAsset mesh)
    {
        var options = Session.Options;
        if (!options.ExportAnimations || options.AnimationExportMode != EAnimationExportMode.ThroughMesh) return;

        if (!mesh.Skeleton.TryLoad<USkeleton>(out var skeleton))
        {
            Log.Debug("Mesh has no valid skeleton, skipping animation collection");
            return;
        }

        var skeletonPath = skeleton.GetPathName();
        var provider = mesh.Owner?.Provider;
        if (provider == null)
        {
            Log.Debug("Mesh has no provider, skipping animation collection");
            return;
        }

        string? animationsFolder = options.ExportFolderMode switch
        {
            EExportFolderMode.BySkeleton => GroupedExportHelper.GetSkeletonAnimationsFolder(skeleton),
            EExportFolderMode.ByModel => OutputFolderOverride,
            _ => null
        };

        Log.Information("Collecting animations for skeleton '{SkeletonName}'", skeleton.Name);

        var exportedCount = 0;
        var processedFiles = 0;
        var addedAnimations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var gameFile in provider.Files.Values)
            {
                if (gameFile == null || !gameFile.Path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    if (!provider.TryLoadPackage(gameFile, out var package)) continue;

                    processedFiles++;
                    if (processedFiles % 500 == 0)
                    {
                        Log.Debug("Animation scan progress: {Processed} files scanned, {Found} animations found", processedFiles, exportedCount);
                    }

                    for (var i = 0; i < package.ExportMapLength; i++)
                    {
                        try
                        {
                            var pointer = new FPackageIndex(package, i + 1).ResolvedObject;
                            if (pointer?.Object == null) continue;

                            var exportType = pointer.Class?.Object?.Value;
                            if (exportType == null) continue;

                            var exportTypeName = exportType.Name;
                            var isAnimationType = exportTypeName.Contains("AnimSequence") ||
                                                  exportTypeName.Contains("AnimMontage") ||
                                                  exportTypeName.Contains("AnimComposite");
                            if (!isAnimationType) continue;

                            if (options.FilterAnimMontage && exportTypeName.Contains("AnimMontage"))
                            {
                                Log.Debug("Filtered AnimMontage '{Name}'", pointer.Name);
                                continue;
                            }

                            var export = new FPackageIndex(package, i + 1).Load();
                            if (export == null) continue;

                            UAnimationAsset animAsset;
                            if (export is UAnimSequence animSequence && IsSkeletonMatch(animSequence.Skeleton, skeletonPath))
                                animAsset = animSequence;
                            else if (export is UAnimMontage animMontage && !options.FilterAnimMontage && IsSkeletonMatch(animMontage.Skeleton, skeletonPath))
                                animAsset = animMontage;
                            else if (export is UAnimComposite animComposite && IsSkeletonMatch(animComposite.Skeleton, skeletonPath))
                                animAsset = animComposite;
                            else continue;

                            var animPath = animAsset.GetPathName();
                            if (!addedAnimations.Add(animPath)) continue;

                            var animExporter = new AnimationExporter(animAsset)
                            {
                                OutputFolderOverride = animationsFolder
                            };
                            Session.Add(animExporter);
                            exportedCount++;
                        }
                        catch (Exception ex)
                        {
                            Log.Debug(ex, "Failed to process export in package '{PackagePath}'", gameFile.Path);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Failed to process file '{FilePath}'", gameFile.Path);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error while collecting animations for mesh '{MeshName}'", ObjectName);
        }

        Log.Information(exportedCount > 0
            ? "Queued {Count} animation(s) matching skeleton '{SkeletonName}'"
            : "No animations found matching skeleton '{SkeletonName}'", exportedCount, skeleton.Name);
    }

    private bool IsSkeletonMatch(FPackageIndex? animSkeletonRef, string meshSkeletonPath)
    {
        return animSkeletonRef != null &&
               animSkeletonRef.TryLoad<USkeleton>(out var animSkeleton) &&
               string.Equals(animSkeleton.GetPathName(), meshSkeletonPath, StringComparison.OrdinalIgnoreCase);
    }
}