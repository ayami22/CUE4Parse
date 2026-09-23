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
        return format.BuildSkeletalMesh(ObjectName, ObjectPath, Session.Options, dto, materialPaths);
    }

    // Write the model before indexing/collecting animations, so the first output does not
    // wait for a full game scan. Dependencies are exported one at a time in streaming mode.
    protected override void AfterExport(CancellationToken ct)
    {
        var options = Session.Options;
        if (!options.ExportAnimations || options.AnimationExportMode != EAnimationExportMode.ThroughMesh) return;
        if (!originalMesh.Skeleton.TryLoad<USkeleton>(out var skeleton) || originalMesh.Owner?.Provider is not { } provider) return;

        var folder = options.ExportFolderMode switch
        {
            EExportFolderMode.BySkeleton => GroupedExportHelper.GetSkeletonAnimationsFolder(skeleton),
            EExportFolderMode.ByModel => OutputFolderOverride,
            _ => null
        };
        var index = Session.GetAnimationIndex(provider, ct);
        foreach (var entry in index.Find(skeleton.GetPathName(), options.FilterAnimMontage))
        {
            ct.ThrowIfCancellationRequested();
            if (!provider.TryLoadPackageObject<UAnimationAsset>(entry.ObjectPath, out var animation))
            {
                Log.Warning("Could not load indexed animation {AnimationPath}", entry.ObjectPath);
                continue;
            }
            Session.Add(new AnimationExporter(animation) { OutputFolderOverride = folder });
        }
    }
}
