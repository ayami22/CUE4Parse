using System.ComponentModel;

namespace CUE4Parse_Conversion.Options;

public enum EExportFolderMode
{
    [Description("Default (No Grouping)")]
    None,

    [Description("By Model (Separate Folder per Model)")]
    ByModel,

    [Description("By Skeleton (Group by Skeleton)")]
    BySkeleton
}
