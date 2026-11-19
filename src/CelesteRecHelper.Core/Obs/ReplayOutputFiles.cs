namespace CelesteRecHelper.Core.Obs;

/// <summary>
/// The files that can be an OBS replay output. Only the top level of the record directory is scanned and only
/// video containers are kept, so the snapshot does not grow with unrelated files or subfolders.
/// </summary>
public static class ReplayOutputFiles
{
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".mkv", ".mp4", ".mov", ".flv", ".ts", ".m4v" };

    public static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static bool IsCandidate(string path) => Extensions.Contains(Path.GetExtension(path));

    public static IEnumerable<string> EnumerateTopLevel(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(IsCandidate).Select(Path.GetFullPath);
}
