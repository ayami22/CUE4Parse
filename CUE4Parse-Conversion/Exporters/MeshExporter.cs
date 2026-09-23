using System;
using System.Collections.Generic;
using CUE4Parse_Conversion.Dto;
using CUE4Parse_Conversion.Formats.Meshes;
using CUE4Parse_Conversion.Options;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Exports.Material;

namespace CUE4Parse_Conversion.Exporters;

public abstract class MeshExporter<T>(T mesh) : ExporterBase(mesh) where T : UObject
{
    protected abstract IReadOnlyList<ExportFile> BuildFiles(T original, IMeshExportFormat format);

    protected override IReadOnlyList<ExportFile> BuildExportFiles(CancellationToken ct = default)
    {
        Log.Debug("Converting mesh to {Format} at {Quality} quality ({NaniteFormat})", Session.Options.MeshFormat, Session.Options.MeshQuality, Session.Options.NaniteMeshFormat);

        ApplyGroupedOutputFolder();
        return BuildFiles(mesh, GetMeshFormat(Session.Options.MeshFormat));
    }

    /// <summary>
    /// Computes the grouped output folder (ByModel / BySkeleton) for this mesh and stores it in
    /// <see cref="ExporterBase.OutputFolderOverride"/> so materials and textures follow the mesh.
    /// </summary>
    private void ApplyGroupedOutputFolder()
    {
        var mode = Session.Options.ExportFolderMode;
        if (mode == EExportFolderMode.None) return;

        var modelName = GroupedExportHelper.SanitizeFolderName(ObjectName);
        if (mode == EExportFolderMode.ByModel)
        {
            OutputFolderOverride = modelName;
        }
        else if (mesh is USkinnedAsset skinned && skinned.Skeleton.TryLoad<USkeleton>(out var skeleton))
        {
            OutputFolderOverride = $"{GroupedExportHelper.HaveSkeletonRoot}/{GroupedExportHelper.GetSkeletonFolderName(skeleton)}/{modelName}";
        }
        else
        {
            OutputFolderOverride = $"{GroupedExportHelper.NoSkeletonRoot}/{modelName}";
        }
    }

    protected Dictionary<string, string>? EnqueueMaterials(params MeshMaterialDto[] materials)
    {
        if (!Session.Options.ExportMaterials) return null;

        // TODO: currently only usda needs such thing, but maybe other formats will need it in the future, so we can keep it for now
        var paths = Session.Options.MeshFormat == EMeshFormat.USD
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : null;

        foreach (var slot in materials)
        {
            if (slot.Material?.TryLoad<UMaterialInterface>(out var material) == true)
            {
                var materialExporter = new MaterialExporter(material)
                {
                    OutputFolderOverride = OutputFolderOverride
                };
                Session.Add(materialExporter);
                if (paths != null)
                {
                    paths[slot.SlotName] = Resolve(material, "usda");
                }
            }
        }

        return paths;
    }

    protected IReadOnlyList<ExportFile> AddActorXMetadata(StaticMeshDto dto, IReadOnlyList<ExportFile> files)
    {
        if (Session.Options.MeshFormat != EMeshFormat.ActorX) return files;
        var meshPaths = files.Select(file => ResolveOutputPath(file).Item2).ToArray();
        var materialPaths = dto.Materials.Select(ResolveMaterialExportPath).ToArray();
        var metadata = ActorXMetadataFormat.BuildStaticMesh(ObjectPath, dto, meshPaths, materialPaths);
        return [.. files, metadata];
    }

    protected IReadOnlyList<ExportFile> AddActorXMetadata(SkeletalMeshDto dto, IReadOnlyList<ExportFile> files)
    {
        if (Session.Options.MeshFormat != EMeshFormat.ActorX) return files;
        var meshPaths = files.Select(file => ResolveOutputPath(file).Item2).ToArray();
        var materialPaths = dto.Materials.Select(ResolveMaterialExportPath).ToArray();
        var metadata = ActorXMetadataFormat.BuildSkeletalMesh(ObjectPath, dto, meshPaths, materialPaths);
        return [.. files, metadata];
    }

    protected IReadOnlyList<ExportFile> AddActorXMetadata(SkeletonDto dto, IReadOnlyList<ExportFile> files)
    {
        if (Session.Options.MeshFormat != EMeshFormat.ActorX) return files;
        var meshPaths = files.Select(file => ResolveOutputPath(file).Item2).ToArray();
        var metadata = ActorXMetadataFormat.BuildSkeleton(ObjectPath, dto, meshPaths);
        return [.. files, metadata];
    }

    private string? ResolveMaterialExportPath(MeshMaterialDto slot)
    {
        if (!Session.Options.ExportMaterials || slot.Material?.TryLoad<UMaterialInterface>(out var material) != true || material is null)
            return null;

        var exporter = new MaterialExporter(material) { OutputFolderOverride = OutputFolderOverride };
        if (OutputFolderOverride is { } folder)
        {
            var saveLeaf = exporter.SavePath[(exporter.SavePath.LastIndexOf('/') + 1)..];
            return Session.ResolveOutputPathInFolder(folder, saveLeaf, "json");
        }

        return Session.ResolveOutputPath(exporter.SavePath, "json");
    }

    private IMeshExportFormat GetMeshFormat(EMeshFormat format) => format switch
    {
        EMeshFormat.ActorX => new ActorXMeshFormat(),
        EMeshFormat.Gltf2 => new GltfMeshFormat(),
        EMeshFormat.UEFormat => new UEFormatMeshFormat(),
        EMeshFormat.USD => new UsdMeshFormat(),
        _ => throw new NotSupportedException($"Mesh export does not support format {format}. Available formats: {string.Join(", ", Enum.GetNames<EMeshFormat>())}")
    };
}
