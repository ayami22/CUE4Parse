using System.Text.Json;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse_Conversion.Dto;

namespace CUE4Parse_Conversion.Formats.Meshes;

/// <summary>
/// Writes the UE relationship information that PSK/PSKX cannot represent as an ActorX sidecar.
/// Paths to exported files are resolved by the owning exporter before this format is called.
/// </summary>
public static class ActorXMetadataFormat
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static ExportFile BuildStaticMesh(string objectPath, StaticMeshDto dto, IReadOnlyList<string> meshPaths, IReadOnlyList<string?> materialPaths)
    {
        var metadata = new
        {
            schemaVersion = 1,
            format = "ActorX",
            assetType = "staticMesh",
            sourceObjectPath = objectPath,
            coordinateSpace = "UnrealEngine source data; ActorX PSK files apply Y-axis mirroring",
            bounds = new { min = Vector(dto.Bounds.Min), max = Vector(dto.Bounds.Max) },
            sockets = BuildStaticSockets(dto.Sockets),
            bodySetupSourceObjectPath = PathOf(dto.BodySetup),
            materials = BuildMaterials(dto.Materials, materialPaths),
            lods = BuildLods(dto.LODs, meshPaths, materialPaths)
        };

        return BuildFile(metadata);
    }

    public static ExportFile BuildSkeletalMesh(string objectPath, SkeletalMeshDto dto, IReadOnlyList<string> meshPaths, IReadOnlyList<string?> materialPaths)
    {
        var metadata = new
        {
            schemaVersion = 1,
            format = "ActorX",
            assetType = "skeletalMesh",
            sourceObjectPath = objectPath,
            coordinateSpace = "UnrealEngine source data; ActorX PSK files apply Y-axis mirroring",
            skeleton = new
            {
                sourceObjectPath = dto.SkeletonPathName,
                name = dto.SkeletonName,
                exportPaths = meshPaths,
                bones = BuildBones(dto.Bones),
                virtualBones = BuildVirtualBones(dto.VirtualBones),
                sockets = BuildSockets(dto.Sockets),
                physicsAssetSourceObjectPath = PathOf(dto.PhysicsAsset)
            },
            materials = BuildMaterials(dto.Materials, materialPaths),
            morphTargets = (dto.MorphTargets ?? []).Select(PathOf),
            lods = BuildLods(dto.LODs, meshPaths, materialPaths)
        };

        return BuildFile(metadata);
    }

    public static ExportFile BuildSkeleton(string objectPath, SkeletonDto dto, IReadOnlyList<string> meshPaths)
    {
        var metadata = new
        {
            schemaVersion = 1,
            format = "ActorX",
            assetType = "skeleton",
            sourceObjectPath = objectPath,
            coordinateSpace = "UnrealEngine source data; ActorX PSK files apply Y-axis mirroring",
            exportPaths = meshPaths,
            bones = BuildBones(dto.Bones),
            virtualBones = BuildVirtualBones(dto.VirtualBones),
            sockets = BuildSockets(dto.Sockets)
        };

        return BuildFile(metadata);
    }

    public static ExportFile BuildAnimation(string objectPath, string? skeletonSourceObjectPath, IReadOnlyList<string> animationPaths)
    {
        var metadata = new
        {
            schemaVersion = 1,
            format = "ActorX",
            assetType = "animation",
            sourceObjectPath = objectPath,
            skeletonSourceObjectPath,
            exportPaths = animationPaths
        };

        return BuildFile(metadata);
    }

    private static object[] BuildMaterials(IReadOnlyList<MeshMaterialDto> materials, IReadOnlyList<string?> materialPaths) =>
        materials.Select((material, index) => (object)new
        {
            slotIndex = index,
            slotName = material.SlotName,
            sourceObjectPath = PathOf(material.Material),
            exportPath = index < materialPaths.Count ? materialPaths[index] : null
        }).ToArray();

    private static object[] BuildLods<TVertex>(IList<MeshLodDto<TVertex>> lods, IReadOnlyList<string> meshPaths,
        IReadOnlyList<string?> materialPaths)
        where TVertex : struct, IMeshVertex =>
        lods.Select((lod, index) => (object)new
        {
            lodIndex = index,
            sourceLodIndex = lod.IsNanite ? null : (uint?)lod.SourceLodIndex,
            isNanite = lod.IsNanite,
            screenSize = lod.ScreenSize,
            exportPath = index < meshPaths.Count ? meshPaths[index] : null,
            vertexCount = lod.Vertices.Length,
            faceCount = lod.Sections.Sum(section => section.NumFaces),
            materialSections = lod.Sections.Select((section, sectionIndex) =>
            {
                var material = lod.Owner.GetMaterial(section);
                var materialIndex = section.MaterialIndex;
                return new
                {
                    sectionIndex,
                    materialSlotIndex = materialIndex,
                    materialSlotName = material?.SlotName ?? $"MaterialSlot_{sectionIndex}",
                    sourceMaterialObjectPath = PathOf(material?.Material),
                    materialExportPath = materialIndex >= 0 && materialIndex < materialPaths.Count
                        ? materialPaths[materialIndex]
                        : null,
                    firstIndex = section.FirstIndex,
                    indexCount = section.NumFaces * 3,
                    faceCount = section.NumFaces
                };
            }).ToArray()
        }).ToArray();

    private static object[] BuildBones(MeshBoneDto[] bones) => bones.Select((bone, index) => (object)new
    {
        index,
        name = bone.Name,
        parentIndex = bone.ParentIndex,
        transform = Transform(bone.Transform)
    }).ToArray();

    private static object[] BuildVirtualBones(CUE4Parse.UE4.Assets.Exports.Animation.FVirtualBone[]? bones) =>
        (bones ?? []).Select(bone => (object)new
        {
            sourceBone = bone.SourceBoneName.Text,
            targetBone = bone.TargetBoneName.Text,
            name = bone.VirtualBoneName.Text
        }).ToArray();

    private static object[] BuildSockets(FPackageIndex[]? sockets)
    {
        if (sockets is not { Length: > 0 }) return [];

        var result = new List<object>(sockets.Length);
        foreach (var socketIndex in sockets)
        {
            if (socketIndex.Load<USkeletalMeshSocket>() is not { } socket) continue;
            result.Add(new
            {
                sourceObjectPath = socket.GetPathName(),
                name = socket.SocketName.Text,
                boneName = socket.BoneName.Text,
                relativeLocation = Vector(socket.RelativeLocation),
                relativeRotation = new { pitch = socket.RelativeRotation.Pitch, yaw = socket.RelativeRotation.Yaw, roll = socket.RelativeRotation.Roll },
                relativeScale = Vector(socket.RelativeScale)
            });
        }

        return [.. result];
    }

    private static object[] BuildStaticSockets(FPackageIndex[]? sockets)
    {
        if (sockets is not { Length: > 0 }) return [];

        var result = new List<object>(sockets.Length);
        foreach (var socketIndex in sockets)
        {
            if (socketIndex.Load<UStaticMeshSocket>() is not { } socket) continue;
            result.Add(new
            {
                sourceObjectPath = socket.GetPathName(),
                name = socket.SocketName.Text,
                relativeLocation = Vector(socket.RelativeLocation),
                relativeRotation = new { pitch = socket.RelativeRotation.Pitch, yaw = socket.RelativeRotation.Yaw, roll = socket.RelativeRotation.Roll },
                relativeScale = Vector(socket.RelativeScale)
            });
        }

        return [.. result];
    }

    private static string? PathOf(FPackageIndex? index) => index?.ResolvedObject?.GetPathName();

    private static object Transform(FTransform transform) => new
    {
        translation = Vector(transform.Translation),
        rotation = new { x = transform.Rotation.X, y = transform.Rotation.Y, z = transform.Rotation.Z, w = transform.Rotation.W },
        scale = Vector(transform.Scale3D)
    };

    private static object Vector(FVector vector) => new { x = vector.X, y = vector.Y, z = vector.Z };

    private static ExportFile BuildFile(object metadata) =>
        new("json", JsonSerializer.SerializeToUtf8Bytes(metadata, JsonOptions), "_actorx_metadata");
}
