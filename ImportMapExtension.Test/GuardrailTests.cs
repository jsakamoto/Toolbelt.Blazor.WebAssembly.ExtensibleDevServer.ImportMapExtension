using ImportMapExtension.Test.Internals;

namespace ImportMapExtension.Test;

/// <summary>
/// A placeholder that is never replaced produces an app that no browser can start. These cover the
/// ways that could happen, and check that each one is reported rather than shipped.
/// <para>
/// Each test builds in a disposable container of its own. They ask for different things of the same
/// project, and a build that reuses what the one before it left in "obj" is not the build that is
/// being described here.
/// </para>
/// </summary>
[Parallelizable(ParallelScope.All)]
public class GuardrailTests
{
    /// <summary>
    /// The build leaves the placeholder alone on purpose: the development server extension fills it
    /// in as the page is served, from the content that ships after the "integrity" member is
    /// removed.
    /// </summary>
    [Test]
    public async Task BuildingLeavesThePlaceholderInPlace()
    {
        await using var container = await SolutionContainer.StartAsync();
        await container.DotNetAsync($"build {SolutionContainer.SampleAppProject} -c Release");

        // The build never copies index.html to "bin"; the served copy is the SDK's intermediate one.
        var generated = (await container.GlobAsync(
            $"{SolutionContainer.SolutionDir}/SampleApp/obj/Release/net10.0/staticwebassets/htmlassetplaceholders/build/*.html")).Single();

        var html = await container.ReadTextAsync(generated);
        html.Contains("'sha256-{importmap}'").IsTrue(message: "The build was expected to leave the placeholder for the development server extension.");
        Digest.ImportMapOf(html).Contains("\"integrity\"").IsTrue(message: "The build was expected to leave the import map as the SDK wrote it.");
    }

    [Test]
    public async Task PublishingWithoutImportMapGenerationFailsWithAnExplanation()
    {
        await using var container = await SolutionContainer.StartAsync();

        var result = await container.DotNetAsync(
            $"build {SolutionContainer.SampleAppProject} -c Release -p:OverrideHtmlAssetPlaceholders=false",
            allowFailure: true);

        result.ExitCode.IsNot(0L, message: $"The build was expected to fail.\n{result.Output}");
        result.Output.Contains("IMCSP001").IsTrue(message: result.Output);
        result.Output.Contains("OverrideHtmlAssetPlaceholders").IsTrue(message: result.Output);
    }

    /// <summary>
    /// Without the development server there is nobody to fill the placeholder in while you develop.
    /// That is a warning rather than an error, because publishing works perfectly well without it.
    /// </summary>
    [Test]
    public async Task BuildingWithoutTheDevServerWarns()
    {
        await using var container = await SolutionContainer.StartAsync();

        const string project = SolutionContainer.SampleAppProject;
        var withDevServer = await container.ReadTextAsync(project);
        var withoutDevServer = withDevServer.Replace(
            "<PackageReference Include=\"Toolbelt.Blazor.WebAssembly.ExtensibleDevServer\" Version=\"$(ExtensibleDevServerVersion)\" PrivateAssets=\"all\" />", "");
        withoutDevServer.IsNot(withDevServer, message: "The reference to remove was not found in the sample app's project file.");
        await container.WriteTextAsync(project, withoutDevServer);

        var result = await container.DotNetAsync($"build {project} -c Release");

        result.Output.Contains("IMCSP004").IsTrue(message: result.Output);
    }

    [Test]
    public async Task BuildingWithTheDevServerDoesNotWarn()
    {
        await using var container = await SolutionContainer.StartAsync();

        var result = await container.DotNetAsync($"build {SolutionContainer.SampleAppProject} -c Release");

        result.Output.Contains("IMCSP004").IsFalse(message: result.Output);
    }

    /// <summary>Turning the policy work off is a deliberate choice, so it does not fail the build.</summary>
    [Test]
    public async Task TurningThePolicyWorkOffLeavesThePlaceholderAndDoesNotFailThePublish()
    {
        const string outputDir = $"{SolutionContainer.SolutionDir}/_publish-csp-disabled";

        await using var container = await SolutionContainer.StartAsync();
        await container.DotNetAsync(
            $"publish {SolutionContainer.SampleAppProject} -c Release -o {outputDir} -p:ImportMapCspEnabled=false");

        var html = await container.ReadTextAsync($"{outputDir}/wwwroot/index.html");
        html.Contains("'sha256-{importmap}'").IsTrue();
    }
}
