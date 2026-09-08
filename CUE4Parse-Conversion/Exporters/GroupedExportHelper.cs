using System.IO;
using CUE4Parse.UE4.Assets.Exports.Animation;

namespace CUE4Parse_Conversion.Exporters;

/// <summary>
/// Shared naming helpers for grouped export modes ("By Model" / "By Skeleton").
/// Keeps folder names consistent between mesh exports and animation collection.
/// </summary>
public static class GroupedExportHelper
{
    public const string HaveSkeletonRoot = "Have_Skeleton";
    public const string NoSkeletonRoot = "No_Skeleton";
    public const string AnimationsFolder = "Animations";

    public static string SanitizeFolderName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name.Replace('/', '_').Replace('\\', '_');
    }

    public static string GetSkeletonFolderName(USkeleton skeleton) => SanitizeFolderName(skeleton.Name);

    /// <summary>Have_Skeleton/&lt;SkeletonName&gt;/Animations</summary>
    public static string GetSkeletonAnimationsFolder(USkeleton skeleton) =>
        $"{HaveSkeletonRoot}/{GetSkeletonFolderName(skeleton)}/{AnimationsFolder}";
}
