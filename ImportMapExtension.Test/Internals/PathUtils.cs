using System.Runtime.CompilerServices;

namespace ImportMapExtension.Test.Internals;

internal static class PathUtils
{
    /// <summary>The folder of the solution, found from the path of this source file.</summary>
    public static string SolutionDir { get; } = GetSolutionDir();

    private static string GetSolutionDir([CallerFilePath] string thisFilePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFilePath)!, "..", ".."));
}
