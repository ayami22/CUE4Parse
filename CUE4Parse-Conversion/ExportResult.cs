using System;

namespace CUE4Parse_Conversion;

public readonly record struct ExportFile(string Extension, byte[] Data, string? NameSuffix = null);

public readonly record struct ExportSummary(int Succeeded, int Failed)
{
    public int Completed => Succeeded + Failed;
}

public sealed record ExportResult(bool Success, string ObjectPath, IReadOnlyList<string>? DiskFilePaths = null, Exception? Error = null)
{
    public static ExportResult Failure(string objectPath, Exception ex) => new(false, objectPath, null, ex);
}

public readonly record struct ExportProgress(int Completed, int Total, ExportResult? LastResult = null)
{
    public float Percentage => Total > 0 ? Completed / (float)Total : -1f;
    public string DisplayText => Total > 0 ? $"{Completed} / {Total}  ({Percentage * 100:F0}%)" : $"{Completed}";
}

