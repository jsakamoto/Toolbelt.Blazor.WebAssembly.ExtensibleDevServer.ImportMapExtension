using System.Security.Cryptography;
using System.Text;
using Toolbelt.Blazor.WebAssembly.ExtensibleDevServer.ImportMapExtension;

namespace ImportMapExtension.Test;

public class ImportMapCspRewriterTests
{
    /// <summary>
    /// The import map content a browser would see, which always has LF newlines whatever the file
    /// on disk uses. Written with explicit escapes so that the newlines of this source file cannot
    /// change what the tests assert.
    /// </summary>
    private const string ImportMapBody =
        "{\n  \"imports\": {\n    \"./App.razor.js\": \"./App.abc123.razor.js\"\n  }\n}";

    /// <summary>
    /// Builds a document around <see cref="ImportMapBody"/> using the given newline, so that a test
    /// can state what a browser would see rather than repeat a literal digest.
    /// </summary>
    private static string DocumentWith(string policy, string newline = "\n", string? body = null)
    {
        var lines = new[]
        {
            "<!DOCTYPE html>",
            "<html>",
            "<head>",
            $"<meta http-equiv=\"Content-Security-Policy\" content=\"script-src 'self' {policy};\" />",
            $"<script type=\"importmap\">{body ?? ImportMapBody}</script>",
            "</head>",
            "<body></body>",
            "</html>",
        };
        return string.Join("\n", lines).Replace("\n", newline);
    }

    /// <summary>Computes the digest independently of the code under test.</summary>
    private static string DigestOf(string body, string algorithm = "sha256")
    {
        using HashAlgorithm hash = algorithm switch { "sha384" => SHA384.Create(), "sha512" => SHA512.Create(), _ => SHA256.Create() };
        return $"{algorithm}-{Convert.ToBase64String(hash.ComputeHash(new UTF8Encoding(false).GetBytes(body)))}";
    }

    [Test]
    public void Rewrite_ReplacesThePlaceholderWithTheDigestOfTheImportMap()
    {
        var result = ImportMapCspRewriter.Rewrite(DocumentWith("'sha256-{importmap}'"));

        result.Succeeded.IsTrue();
        result.Changed.IsTrue();
        result.Hashes.Is(DigestOf(ImportMapBody));
        result.Html!.Contains($"'{DigestOf(ImportMapBody)}'").IsTrue();
        result.Html.Contains("{importmap}").IsFalse();
    }

    /// <summary>
    /// The HTML parser turns the newlines of the input stream into LF before it tokenizes anything,
    /// so a document saved with CRLF has to produce the same digest as one saved with LF. Getting
    /// this wrong yields a policy that looks right and that no browser accepts.
    /// </summary>
    [Test]
    public void Rewrite_GivesTheSameDigestForCrLfAndLf()
    {
        var fromLf = ImportMapCspRewriter.Rewrite(DocumentWith("'sha256-{importmap}'", "\n"));
        var fromCrLf = ImportMapCspRewriter.Rewrite(DocumentWith("'sha256-{importmap}'", "\r\n"));

        fromLf.Hashes.Is(DigestOf(ImportMapBody));
        fromCrLf.Hashes.Is(fromLf.Hashes);
    }

    [Test]
    public void Rewrite_TreatsALoneCarriageReturnAsANewlineToo()
    {
        ImportMapCspRewriter.Rewrite(DocumentWith("'sha256-{importmap}'", "\r")).Hashes.Is(DigestOf(ImportMapBody));
    }

    [TestCase("sha256")]
    [TestCase("sha384")]
    [TestCase("sha512")]
    public void Rewrite_UsesTheAlgorithmNamedInFrontOfThePlaceholder(string algorithm)
    {
        ImportMapCspRewriter.Rewrite(DocumentWith($"'{algorithm}-{{importmap}}'")).Hashes.Is(DigestOf(ImportMapBody, algorithm));
    }

    [Test]
    public void Rewrite_ReplacesEveryOccurrenceOfThePlaceholder()
    {
        var html = DocumentWith("'sha256-{importmap}'").Replace("<body></body>", "<!-- also here: 'sha384-{importmap}' -->");

        var result = ImportMapCspRewriter.Rewrite(html);

        result.Html!.Contains(DigestOf(ImportMapBody)).IsTrue();
        result.Html.Contains(DigestOf(ImportMapBody, "sha384")).IsTrue();
        result.Html.Contains("{importmap}").IsFalse();
    }

    [Test]
    public void Rewrite_LeavesADocumentWithoutThePlaceholderAlone()
    {
        var result = ImportMapCspRewriter.Rewrite(DocumentWith("'self'"));

        result.Succeeded.IsTrue();
        result.Changed.IsFalse();
        result.Html.IsNull();
    }

    [TestCase("<script type='importmap'>{BODY}</script>")]
    [TestCase("<script type = \"importmap\" >{BODY}</script >")]
    [TestCase("<script data-role=\"boot\" type=\"importmap\" defer>{BODY}</script>")]
    public void Rewrite_FindsTheImportMapHoweverTheTagIsWritten(string element)
    {
        var html = $"<html><head><meta content=\"'sha256-{{importmap}}'\" />{element.Replace("{BODY}", ImportMapBody)}</head></html>";

        ImportMapCspRewriter.Rewrite(html).Hashes.Is(DigestOf(ImportMapBody));
    }

    [Test]
    public void Rewrite_IgnoresAScriptThatIsNotAnImportMap()
    {
        var html = "<html><head><meta content=\"'sha256-{importmap}'\" />" +
                   "<script type=\"module\">console.log(1)</script>" +
                   $"<script type=\"importmap\">{ImportMapBody}</script></head></html>";

        ImportMapCspRewriter.Rewrite(html).Hashes.Is(DigestOf(ImportMapBody));
    }

    [Test]
    public void Rewrite_FailsWhenThereIsNoImportMapToHash()
    {
        var html = "<html><head><meta content=\"'sha256-{importmap}'\" /></head></html>";

        ImportMapCspRewriter.Rewrite(html).Error.Is(RewriteError.NoImportMapElement);
    }

    [Test]
    public void Rewrite_FailsWhenThereIsMoreThanOneImportMap()
    {
        var html = DocumentWith("'sha256-{importmap}'").Replace("<body></body>", $"<script type=\"importmap\">{ImportMapBody}</script>");

        ImportMapCspRewriter.Rewrite(html).Error.Is(RewriteError.MultipleImportMapElements);
    }

    [Test]
    public void Rewrite_FailsWhenThePlaceholderHasNoAlgorithmInFrontOfIt()
    {
        ImportMapCspRewriter.Rewrite(DocumentWith("'{importmap}'")).Error.Is(RewriteError.MissingAlgorithmPrefix);
    }

    [Test]
    public void Rewrite_AcceptsACustomPlaceholder()
    {
        ImportMapCspRewriter.Rewrite(DocumentWith("'sha256-@@MAP@@'"), "@@MAP@@").Hashes.Is(DigestOf(ImportMapBody));
    }

    [Test]
    public void Rewrite_KeepsEverythingOutsideThePlaceholderByteForByte()
    {
        var html = DocumentWith("'sha256-{importmap}'", "\r\n");

        var result = ImportMapCspRewriter.Rewrite(html);

        // Only the token is gone; the CRLF newlines of the document itself are untouched.
        result.Html!.Replace(DigestOf(ImportMapBody), "{importmap}").Is(html.Replace("sha256-{importmap}", "{importmap}"));
    }

    [Test]
    public void FindImportMap_ReturnsNothingWhenThereIsNotExactlyOne()
    {
        ImportMapCspRewriter.FindImportMap("<html></html>").IsNull();
        ImportMapCspRewriter.FindImportMap(DocumentWith("'self'").Replace("<body></body>", $"<script type=\"importmap\">{ImportMapBody}</script>")).IsNull();
        ImportMapCspRewriter.FindImportMap(DocumentWith("'self'"))!.Body.Is(ImportMapBody);
    }

    [Test]
    public void ReplaceImportMapBody_TouchesNothingButTheImportMap()
    {
        var html = DocumentWith("'self'");

        var replaced = ImportMapCspRewriter.ReplaceImportMapBody(html, "{}");

        replaced.Is(html.Replace(ImportMapBody, "{}"));
    }
}
