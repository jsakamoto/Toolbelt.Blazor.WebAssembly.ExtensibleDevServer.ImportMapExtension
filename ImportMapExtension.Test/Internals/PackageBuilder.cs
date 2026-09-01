using Toolbelt.Diagnostics;

namespace ImportMapExtension.Test.Internals;

internal static class PackageBuilder
{
    public static async Task PackAsync(string relativeProjectPath)
    {
        // Run "dotnet pack" to create the NuGet package.
        //
        // Node reuse is turned off because this package ships an MSBuild task, and MSBuild keeps a
        // task assembly loaded in the worker process that used it. A node that outlived this run
        // would hold the freshly built assembly open and fail the next one with "access to the path
        // is denied".
        var projectPath = Path.Combine([PathUtils.SolutionDir, .. relativeProjectPath.Split('/')]);
        using var process = await XProcess
            .Start("dotnet", $"pack \"{projectPath}\" -c Release -nodeReuse:false")
            .WaitForExitAsync();

        process.ExitCode.Is(0, message: $"\"dotnet pack\" failed for \"{projectPath}\" (exit code {process.ExitCode}).\n{process.Output}");
    }
}
