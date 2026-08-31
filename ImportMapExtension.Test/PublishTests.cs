using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ImportMapExtension.Test.Internals;

namespace ImportMapExtension.Test;

/// <summary>
/// Publishes the sample app with the packed package and inspects what a static host would serve.
/// </summary>
public class PublishTests
{
    [Test]
    public async Task ThePolicyCarriesTheDigestOfTheImportMapThatWasPublishedWithIt()
    {
        var html = await File.ReadAllTextAsync(await PackagedSolution.PublishedIndexHtmlAsync());

        Digest.InPolicyOf(html).Is(Digest.OfImportMapIn(html));
    }

    /// <summary>
    /// Only development needs the "integrity" member gone. What ships keeps it, because there the
    /// import map and the files it names are generated together and always agree.
    /// </summary>
    [Test]
    public async Task ThePublishedImportMapKeepsItsIntegrity()
    {
        var html = await File.ReadAllTextAsync(await PackagedSolution.PublishedIndexHtmlAsync());

        Digest.ImportMapOf(html).Contains("\"integrity\"").IsTrue();
    }

    [Test]
    public async Task NoPlaceholderSurvivesIntoThePublishedOutput()
    {
        foreach (var file in Directory.GetFiles(await PackagedSolution.PublishedWwwRootAsync(), "*.html", SearchOption.AllDirectories))
        {
            (await File.ReadAllTextAsync(file)).Contains("{importmap}")
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
        var indexHtml = await PackagedSolution.PublishedIndexHtmlAsync();
        var compressed = indexHtml + extension;

        File.Exists(compressed).IsTrue(message: $"The SDK did not produce \"{compressed}\".");

        using var source = File.OpenRead(compressed);
        using Stream decompressor = extension == ".gz"
            ? new GZipStream(source, CompressionMode.Decompress)
            : new BrotliStream(source, CompressionMode.Decompress);
        using var reader = new StreamReader(decompressor, Encoding.UTF8);

        (await reader.ReadToEndAsync()).Is(await File.ReadAllTextAsync(indexHtml));
    }

    /// <summary>
    /// The endpoint manifest is what an ASP.NET Core host serving this app answers with, down to
    /// the length and the ETag, so it has to describe the rewritten file.
    /// </summary>
    [Test]
    public async Task TheEndpointManifestDescribesTheRewrittenFile()
    {
        var publishDir = await PackagedSolution.PublishedDirAsync;
        var manifestPath = Directory.GetFiles(publishDir, "*.staticwebassets.endpoints.json").Single();

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        var lengths = manifest.RootElement.GetProperty("Endpoints").EnumerateArray()
            .Where(endpoint => endpoint.GetProperty("Route").GetString() == "index.html")
            .Where(endpoint => !endpoint.GetProperty("ResponseHeaders").EnumerateArray()
                .Any(header => header.GetProperty("Name").GetString() == "Content-Encoding"))
            .Select(endpoint => endpoint.GetProperty("ResponseHeaders").EnumerateArray()
                .First(header => header.GetProperty("Name").GetString() == "Content-Length")
                .GetProperty("Value").GetString())
            .ToArray();

        lengths.IsNot([], message: "The manifest has no uncompressed endpoint for \"index.html\".");
        foreach (var length in lengths)
        {
            length.Is(new FileInfo(await PackagedSolution.PublishedIndexHtmlAsync()).Length.ToString());
        }
    }

    [Test]
    public async Task TheDigestIsWrittenToAFileWhenOneIsAskedFor()
    {
        var outputDir = Path.Combine(PackagedSolution.WorkRoot, "hashfile");
        var hashFile = Path.Combine(outputDir, "csp-hash.txt");

        await PackagedSolution.PublishSampleAppAsync(outputDir, $"-p:ImportMapCspHashOutputFile=\"{hashFile}\"");

        File.Exists(hashFile).IsTrue();
        var written = (await File.ReadAllLinesAsync(hashFile)).Single();
        written.Is(Digest.OfImportMapIn(await File.ReadAllTextAsync(Path.Combine(outputDir, "wwwroot", "index.html"))));
    }
}
