using Toolbelt;

namespace ImportMapExtension.Test.Internals;

internal static class PathUtils
{
    /// <summary>The folder of the solution.</summary>
    public static readonly string SolutionDir = FileIO.FindContainerDirToAncestor("*.slnx");
}
