using Toolbelt;
using Toolbelt.Diagnostics;

namespace ImportMapExtension.Test.Internals;

/// <summary>
/// Packs this package and hands out a copy of the solution to run the sample app from. The work
/// happens on first use and is shared by every test that needs it, so the unit tests stay fast when
/// they are run on their own.
/// <para>
/// Every run uses a package version of its own. NuGet extracts a package into the global cache by
/// version and never looks at it again, so reusing a version would quietly test whatever was packed
/// the first time.
/// </para>
/// </summary>
internal static class PackagedSolution
{
    public static string PackageVersion { get; } = $"10.0.11-test.{DateTime.UtcNow:yyyyMMddHHmmss}";

    private static readonly Lazy<Task> _Packed = new(PackAsyncCore);

    private static readonly Lazy<Task<string>> _PublishedDir = new(PublishOnceAsync);

    public static string WorkRoot { get; } = Path.Combine(Path.GetTempPath(), "ImportMapExtension.Test", PackageVersion);

    public static Task PackAsync() => _Packed.Value;

    /// <summary>The folder that "dotnet publish" of the sample app wrote to.</summary>
    public static Task<string> PublishedDirAsync => _PublishedDir.Value;

    public static async Task<string> PublishedWwwRootAsync() => Path.Combine(await PublishedDirAsync, "wwwroot");

    public static async Task<string> PublishedIndexHtmlAsync() => Path.Combine(await PublishedWwwRootAsync(), "index.html");

    private static async Task PackAsyncCore()
    {
        var distDir = Path.Combine(PathUtils.SolutionDir, "_dist");
        foreach (var stale in Directory.GetFiles(distDir, "*.nupkg")) File.Delete(stale);

        await RunAsync("dotnet",
            $"pack \"{Path.Combine(PathUtils.SolutionDir, "ImportMapExtension.Package", "ImportMapExtension.Package.csproj")}\" " +
            $"-c Release -p:ImportMapExtensionVersion={PackageVersion}");
    }

    private static async Task<string> PublishOnceAsync()
    {
        var publishDir = Path.Combine(WorkRoot, "publish");
        await PublishSampleAppAsync(publishDir);
        return publishDir;
    }

    public static async Task PublishSampleAppAsync(string outputDir, string extraArguments = "")
    {
        await PackAsync();
        var project = Path.Combine(PathUtils.SolutionDir, "SampleApp", "SampleApp.csproj");
        await RunAsync("dotnet",
            $"publish \"{project}\" -c Release -o \"{outputDir}\" -p:ImportMapExtensionVersion={PackageVersion} {extraArguments}");
    }

    /// <summary>
    /// Copies the whole solution into a temporary folder, so that a test can edit the sample app's
    /// sources without touching the working tree. The "_dist" folder comes along, so the package
    /// packed above is what the copy restores.
    /// </summary>
    public static async Task<WorkDirectory> CreateWorkspaceAsync()
    {
        await PackAsync();
        return WorkDirectory.CreateCopyFrom(PathUtils.SolutionDir, entry => entry.Name is not "bin" and not "obj" and not ".vs" and not ".git");
    }

    /// <summary>
    /// Runs a command and fails the test with its whole output when it does not succeed. MSBuild
    /// node reuse is turned off so that the packed task assembly is not held open by a node that
    /// outlives the test run.
    /// </summary>
    public static async Task<XProcess> RunAsync(string command, string arguments, bool allowFailure = false, string? workingDirectory = null)
    {
        var process = await XProcess
            .Start(command, arguments + " -nodeReuse:false", workingDirectory ?? PathUtils.SolutionDir)
            .WaitForExitAsync();

        if (!allowFailure)
        {
            process.ExitCode.Is(0, message: $"\"{command} {arguments}\" failed (exit code {process.ExitCode}).\n{process.Output}");
        }
        return process;
    }
}
