using ImportMapExtension.Test.Internals;

// Each of the tests that builds, publishes or runs the sample app does so in a container of its
// own, and every one of those containers is a busy one. This is how many of them are allowed to be
// busy at the same time.
[assembly: LevelOfParallelism(3)]

// Not in any namespace: NUnit runs a [SetUpFixture]'s [OneTimeSetUp] once for the entire assembly
// when the fixture itself has no namespace, regardless of which single test ends up being run.
[SetUpFixture]
public class AssemblySetup
{
    [OneTimeSetUp]
    public async Task PackPackageAndInstallBrowserOnce()
    {
        // Delete all *.nupkg files in the "_dist" folder to ensure that the test runs with a clean slate and does not pick up stale content from previous runs.
        var distDir = Path.Combine(PathUtils.SolutionDir, "_dist");
        Directory.GetFiles(distDir, "*.nupkg").ToList().ForEach(File.Delete);

        // Neither of these needs the other, and on a machine that has done neither before, the
        // browser is a download and the package is a build.
        await Task.WhenAll(
            PackageBuilder.PackAsync("ImportMapExtension.Package/ImportMapExtension.Package.csproj"),
            Task.Run(HeadlessBrowser.InstallChromium)
        );
    }
}
