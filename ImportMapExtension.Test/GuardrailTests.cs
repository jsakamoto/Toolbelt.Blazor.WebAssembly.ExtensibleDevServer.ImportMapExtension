using ImportMapExtension.Test.Internals;

namespace ImportMapExtension.Test;

/// <summary>
/// A placeholder that is never replaced produces an app that no browser can start. These cover the
/// ways that could happen, and check that each one is reported rather than shipped.
/// </summary>
public class GuardrailTests
{
    private static string SampleAppProject => Path.Combine(PathUtils.SolutionDir, "SampleApp", "SampleApp.csproj");

    /// <summary>
    /// The build leaves the placeholder alone on purpose: the development server extension fills it
    /// in as the page is served, from the content that ships after the "integrity" member is
    /// removed.
    /// </summary>
    [Test]
    public async Task BuildingLeavesThePlaceholderInPlace()
    {
        await PackagedSolution.PackAsync();

        var outputDir = Path.Combine(PackagedSolution.WorkRoot, "build-only");
        await PackagedSolution.RunAsync("dotnet",
            $"build \"{SampleAppProject}\" -c Release -o \"{outputDir}\" -p:ImportMapExtensionVersion={PackagedSolution.PackageVersion}");

        // The build never copies index.html to "bin"; the served copy is the SDK's intermediate one.
        var generated = Directory
            .GetFiles(Path.Combine(PathUtils.SolutionDir, "SampleApp", "obj", "Release", "net10.0", "staticwebassets", "htmlassetplaceholders", "build"), "*.html")
            .Single();

        var html = await File.ReadAllTextAsync(generated);
        html.Contains("'sha256-{importmap}'").IsTrue(message: "The build was expected to leave the placeholder for the development server extension.");
        Digest.ImportMapOf(html).Contains("\"integrity\"").IsTrue(message: "The build was expected to leave the import map as the SDK wrote it.");
    }

    [Test]
    public async Task PublishingWithoutImportMapGenerationFailsWithAnExplanation()
    {
        await PackagedSolution.PackAsync();

        var outputDir = Path.Combine(PackagedSolution.WorkRoot, "no-importmap");
        var process = await PackagedSolution.RunAsync("dotnet",
            $"build \"{SampleAppProject}\" -c Release -o \"{outputDir}\" " +
            $"-p:ImportMapExtensionVersion={PackagedSolution.PackageVersion} -p:OverrideHtmlAssetPlaceholders=false",
            allowFailure: true);

        process.ExitCode.IsNot(0, message: $"The build was expected to fail.\n{process.Output}");
        process.Output.Contains("IMCSP001").IsTrue(message: process.Output);
        process.Output.Contains("OverrideHtmlAssetPlaceholders").IsTrue(message: process.Output);
    }

    /// <summary>
    /// Without the development server there is nobody to fill the placeholder in while you develop.
    /// That is a warning rather than an error, because publishing works perfectly well without it.
    /// </summary>
    [Test]
    public async Task BuildingWithoutTheDevServerWarns()
    {
        await PackagedSolution.PackAsync();

        using var workspace = await PackagedSolution.CreateWorkspaceAsync();
        var project = Path.Combine(workspace, "SampleApp", "SampleApp.csproj");
        var text = await File.ReadAllTextAsync(project);
        await File.WriteAllTextAsync(project, text.Replace(
            "<PackageReference Include=\"Toolbelt.Blazor.WebAssembly.ExtensibleDevServer\" Version=\"$(ExtensibleDevServerVersion)\" PrivateAssets=\"all\" />", ""));

        var process = await PackagedSolution.RunAsync("dotnet",
            $"build \"{project}\" -c Release -p:ImportMapExtensionVersion={PackagedSolution.PackageVersion}",
            workingDirectory: workspace);

        process.Output.Contains("IMCSP004").IsTrue(message: process.Output);
    }

    [Test]
    public async Task BuildingWithTheDevServerDoesNotWarn()
    {
        await PackagedSolution.PackAsync();

        var process = await PackagedSolution.RunAsync("dotnet",
            $"build \"{SampleAppProject}\" -c Release -p:ImportMapExtensionVersion={PackagedSolution.PackageVersion}");

        process.Output.Contains("IMCSP004").IsFalse(message: process.Output);
    }

    /// <summary>Turning the policy work off is a deliberate choice, so it does not fail the build.</summary>
    [Test]
    public async Task TurningThePolicyWorkOffLeavesThePlaceholderAndDoesNotFailThePublish()
    {
        var outputDir = Path.Combine(PackagedSolution.WorkRoot, "csp-disabled");
        await PackagedSolution.PublishSampleAppAsync(outputDir, "-p:ImportMapCspEnabled=false");

        var html = await File.ReadAllTextAsync(Path.Combine(outputDir, "wwwroot", "index.html"));
        html.Contains("'sha256-{importmap}'").IsTrue();
    }
}
