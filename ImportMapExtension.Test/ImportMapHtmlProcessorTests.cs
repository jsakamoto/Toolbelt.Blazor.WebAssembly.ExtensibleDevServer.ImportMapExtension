using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Toolbelt.Blazor.WebAssembly.ExtensibleDevServer.ImportMapExtension;

namespace ImportMapExtension.Test;

/// <summary>
/// What the development server extension does to a document, without an HTTP pipeline in the way.
/// </summary>
public class ImportMapHtmlProcessorTests
{
    private const string MapWithIntegrity =
        "{\n" +
        "  \"imports\": {\n    \"./App.razor.js\": \"./App.abc123.razor.js\"\n  },\n" +
        "  \"scopes\": {},\n" +
        "  \"integrity\": {\n    \"./App.abc123.razor.js\": \"sha256-AAAA\"\n  }\n" +
        "}";

    private static string DocumentWith(string body, string policy = "'sha256-{importmap}'") =>
        "<!DOCTYPE html>\n<html>\n<head>\n" +
        $"<meta http-equiv=\"Content-Security-Policy\" content=\"script-src 'self' {policy};\" />\n" +
        $"<script type=\"importmap\">{body}</script>\n" +
        "</head>\n<body></body>\n</html>";

    /// <summary>
    /// The digest a browser computes: over the text after the HTML parser has turned every newline
    /// into LF.
    /// </summary>
    private static string DigestOf(string body)
    {
        using var sha256 = SHA256.Create();
        var normalized = body.Replace("\r\n", "\n").Replace("\r", "\n");
        return "sha256-" + Convert.ToBase64String(sha256.ComputeHash(new UTF8Encoding(false).GetBytes(normalized)));
    }

    /// <summary>Reads the import map back out of a processed document.</summary>
    private static string ImportMapOf(string html) => ImportMapCspRewriter.FindImportMap(html)!.Body;

    [Test]
    public void Process_RemovesTheIntegrityMemberAndKeepsTheRest()
    {
        var result = new ImportMapHtmlProcessor("{importmap}", stripIntegrity: true).Process(DocumentWith(MapWithIntegrity));

        result.IsNotNull();

        using var map = JsonDocument.Parse(ImportMapOf(result!));
        map.RootElement.TryGetProperty("integrity", out _).IsFalse();
        map.RootElement.TryGetProperty("imports", out var imports).IsTrue();
        imports.GetProperty("./App.razor.js").GetString().Is("./App.abc123.razor.js");
        map.RootElement.TryGetProperty("scopes", out _).IsTrue();
    }

    /// <summary>
    /// The whole point of doing both jobs in one pass: the policy has to describe the import map
    /// that actually ships, not the one that arrived.
    /// </summary>
    [Test]
    public void Process_WritesTheDigestOfTheImportMapThatSurvivesTheRemoval()
    {
        var result = new ImportMapHtmlProcessor("{importmap}", stripIntegrity: true).Process(DocumentWith(MapWithIntegrity))!;

        result.Contains($"'{DigestOf(ImportMapOf(result))}'").IsTrue();
        result.Contains("{importmap}").IsFalse();
    }

    /// <summary>
    /// The bytes that go out and the bytes that get hashed have to be the same thing, so the import
    /// map is written with LF whatever machine this runs on.
    /// </summary>
    [Test]
    public void Process_WritesTheImportMapWithLfNewlines()
    {
        var result = new ImportMapHtmlProcessor("{importmap}", stripIntegrity: true).Process(DocumentWith(MapWithIntegrity))!;

        ImportMapOf(result).Contains('\r').IsFalse();
    }

    [Test]
    public void Process_LeavesTheIntegrityAloneWhenAskedTo()
    {
        var result = new ImportMapHtmlProcessor("{importmap}", stripIntegrity: false).Process(DocumentWith(MapWithIntegrity))!;

        ImportMapOf(result).Is(MapWithIntegrity);
        result.Contains($"'{DigestOf(MapWithIntegrity)}'").IsTrue();
    }

    /// <summary>
    /// A document with no placeholder is none of this extension's business as far as the policy is
    /// concerned, but the import map still has to be repaired.
    /// </summary>
    [Test]
    public void Process_StillRemovesTheIntegrityWhenThereIsNoPlaceholder()
    {
        var result = new ImportMapHtmlProcessor("{importmap}", stripIntegrity: true).Process(DocumentWith(MapWithIntegrity, "'self'"));

        result.IsNotNull();
        ImportMapOf(result!).Contains("integrity").IsFalse();
        result!.Contains("script-src 'self' 'self';").IsTrue();
    }

    [Test]
    public void Process_ReturnsNothingWhenThereIsNothingToDo()
    {
        var mapWithoutIntegrity = "{\n  \"imports\": {}\n}";

        new ImportMapHtmlProcessor("{importmap}", stripIntegrity: true)
            .Process(DocumentWith(mapWithoutIntegrity, "'self'"))
            .IsNull();
    }

    [Test]
    public void Process_ReturnsNothingForADocumentWithoutAnImportMap()
    {
        new ImportMapHtmlProcessor("{importmap}", stripIntegrity: true)
            .Process("<html><head></head><body></body></html>")
            .IsNull();
    }

    [Test]
    public void Process_SurvivesAnImportMapThatIsNotValidJson()
    {
        var result = new ImportMapHtmlProcessor("{importmap}", stripIntegrity: true).Process(DocumentWith("not json at all"));

        // The policy is still written, from the content as it stands.
        result!.Contains($"'{DigestOf("not json at all")}'").IsTrue();
    }
}
