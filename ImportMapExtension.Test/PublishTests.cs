using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ImportMapExtension.Test.Internals;

namespace ImportMapExtension.Test;

/// <summary>
/// Publishes the sample app with the packed package inside a disposable container, and inspects what
/// a static host would serve.
/// </summary>
[Parallelizable(ParallelScope.All)]
public class PublishTests
{
    private const string PublishDir = $"{SolutionContainer.SolutionDir}/_publish";

    private const string PublishedIndexHtml = $"{PublishDir}/wwwroot/index.html";

    private SolutionContainer _Container = null!;

    /// <summary>
    /// One publish is all the tests below need, and none of them writes anything, so they share it.
    /// </summary>
    [OneTimeSetUp]
    public async Task PublishTheSampleApp()
    {
        this._Container = await SolutionContainer.StartAsync();
        await this._Container.DotNetAsync($"publish {SolutionContainer.SampleAppProject} -c Release -o {PublishDir}");
    }

    [OneTimeTearDown]
    public async Task DisposeTheContainer() => await this._Container.DisposeAsync();

    [Test]
    public async Task ThePolicyCarriesTheDigestOfTheImportMapThatWasPublishedWithIt()
    {
        var html = await this._Container.ReadTextAsync(PublishedIndexHtml);

        Digest.InPolicyOf(html).Is(Digest.OfImportMapIn(html));
    }

    /// <summary>
    /// Only development needs the "integrity" member gone. What ships keeps it, because there the
    /// import map and the files it names are generated together and always agree.
    /// </summary>
    [Test]
    public async Task ThePublishedImportMapKeepsItsIntegrity()
    {
        var html = await this._Container.ReadTextAsync(PublishedIndexHtml);

        Digest.ImportMapOf(html).Contains("\"integrity\"").IsTrue();
    }

    [Test]
    public async Task NoPlaceholderSurvivesIntoThePublishedOutput()
    {
        foreach (var file in await this._Container.FindFilesAsync($"{PublishDir}/wwwroot", "*.html"))
        {
            (await this._Container.ReadTextAsync(file)).Contains("{importmap}")
                .IsFalse(message: $"\"{file}\" still has the placeholder in it.");
        }
    }

    /// <summary>
    /// The SDK pre-compresses the HTML for hosts that serve a compressed copy directly. Rewriting
    /// the published file instead of the intermediate one would leave these two holding the
    /// previous content, and a host that picked one would serve a policy that does not match.
    /// </summary>
    [TestCase(".gz")]
    [TestCase(".br")]
    public async Task ThePreCompressedCopiesHoldTheSameContentAsTheFileTheyCompress(string extension)
    {
        var compressed = PublishedIndexHtml + extension;

        (await this._Container.FileExistsAsync(compressed)).IsTrue(message: $"The SDK did not produce \"{compressed}\".");

        using var source = new MemoryStream(await this._Container.ReadBytesAsync(compressed));
        using Stream decompressor = extension == ".gz"
            ? new GZipStream(source, CompressionMode.Decompress)
            : new BrotliStream(source, CompressionMode.Decompress);
        using var reader = new StreamReader(decompressor, Encoding.UTF8);

        (await reader.ReadToEndAsync()).Is(await this._Container.ReadTextAsync(PublishedIndexHtml));
    }

    /// <summary>
    /// The endpoint manifest is what an ASP.NET Core host serving this app answers with, down to
    /// the length and the ETag, so it has to describe the rewritten file.
    /// </summary>
    [Test]
    public async Task TheEndpointManifestDescribesTheRewrittenFile()
    {
        var manifestPath = (await this._Container.GlobAsync($"{PublishDir}/*.staticwebassets.endpoints.json")).Single();

        using var manifest = JsonDocument.Parse(await this._Container.ReadTextAsync(manifestPath));
        var lengths = manifest.RootElement.GetProperty("Endpoints").EnumerateArray()
            .Where(endpoint => endpoint.GetProperty("Route").GetString() == "index.html")
            .Where(endpoint => !endpoint.GetProperty("ResponseHeaders").EnumerateArray()
                .Any(header => header.GetProperty("Name").GetString() == "Content-Encoding"))
            .Select(endpoint => endpoint.GetProperty("ResponseHeaders").EnumerateArray()
                .First(header => header.GetProperty("Name").GetString() == "Content-Length")
                .GetProperty("Value").GetString())
            .ToArray();

        var publishedLength = (await this._Container.ReadBytesAsync(PublishedIndexHtml)).Length;

        lengths.IsNot([], message: "The manifest has no uncompressed endpoint for \"index.html\".");
        foreach (var length in lengths)
        {
            length.Is(publishedLength.ToString());
        }
    }

    /// <summary>
    /// This one publishes again with a property of its own, and the step that writes the digest only
    /// runs when the SDK regenerates the document it writes into, so it needs a tree the other
    /// publish above has not already been through.
    /// </summary>
    [Test]
    public async Task TheDigestIsWrittenToAFileWhenOneIsAskedFor()
    {
        const string outputDir = $"{SolutionContainer.SolutionDir}/_publish-hashfile";
        const string hashFile = $"{outputDir}/csp-hash.txt";

        await using var container = await SolutionContainer.StartAsync();
        await container.DotNetAsync(
            $"publish {SolutionContainer.SampleAppProject} -c Release -o {outputDir} -p:ImportMapCspHashOutputFile={hashFile}");

        (await container.FileExistsAsync(hashFile)).IsTrue();
        var written = (await container.ReadTextAsync(hashFile)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Single();
        written.Is(Digest.OfImportMapIn(await container.ReadTextAsync($"{outputDir}/wwwroot/index.html")));
    }
}
