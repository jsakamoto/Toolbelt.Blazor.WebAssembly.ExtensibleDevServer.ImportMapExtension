using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Toolbelt.Blazor.WebAssembly.ExtensibleDevServer.ImportMapExtension;

/// <summary>
/// Locates the import map of an HTML document and replaces the Content Security Policy placeholder
/// with the digest of that import map.
/// <para>
/// This type does no file I/O, knows nothing about MSBuild or ASP.NET Core, and compiles for
/// .NET Framework as well, because both halves of this package share it.
/// </para>
/// </summary>
public static class ImportMapCspRewriter
{
    public const string DefaultPlaceholder = "{importmap}";

    /// <summary>
    /// Matches an import map element. The content of a "script" element is raw text, so the first
    /// "&lt;/script" that follows the start tag always ends it.
    /// </summary>
    private static readonly Regex _ImportMapElement = new(
        "<script\\b[^>]*\\btype\\s*=\\s*(?:\"importmap\"|'importmap'|importmap)(?:\\s[^>]*)?>(?<body>.*?)</script\\s*>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly string[] _Algorithms = { "sha256", "sha384", "sha512" };

    /// <summary>
    /// Returns the one import map of the document, or <c>null</c> when it has none or more than one.
    /// </summary>
    public static ImportMapElement? FindImportMap(string html)
    {
        if (html is null) throw new ArgumentNullException(nameof(html));

        var matches = _ImportMapElement.Matches(html);
        if (matches.Count != 1) return null;

        var body = matches[0].Groups["body"];
        return new ImportMapElement(body.Value, body.Index, body.Length);
    }

    public static int CountImportMaps(string html)
    {
        if (html is null) throw new ArgumentNullException(nameof(html));
        return _ImportMapElement.Matches(html).Count;
    }

    /// <summary>
    /// Returns the document with the content of its import map replaced. Everything outside the
    /// import map is left byte for byte as it was.
    /// </summary>
    public static string ReplaceImportMapBody(string html, string newBody)
    {
        if (newBody is null) throw new ArgumentNullException(nameof(newBody));

        var element = FindImportMap(html) ?? throw new InvalidOperationException("The document does not have exactly one import map.");
        return html.Substring(0, element.BodyStart) + newBody + html.Substring(element.BodyStart + element.BodyLength);
    }

    /// <summary>
    /// Replaces every "sha{256,384,512}-{placeholder}" in <paramref name="html"/> with the digest of
    /// the import map that the same document carries.
    /// </summary>
    public static RewriteResult Rewrite(string html, string placeholder = DefaultPlaceholder)
    {
        if (html is null) throw new ArgumentNullException(nameof(html));
        if (string.IsNullOrEmpty(placeholder)) throw new ArgumentException("The placeholder must not be empty.", nameof(placeholder));

        if (html.IndexOf(placeholder, StringComparison.Ordinal) < 0) return RewriteResult.NotApplicable();

        // A document that asks for a digest but carries no import map, or more than one, cannot be
        // given a policy that is both correct and unambiguous.
        var count = CountImportMaps(html);
        if (count == 0) return RewriteResult.Failed(RewriteError.NoImportMapElement);
        if (count > 1) return RewriteResult.Failed(RewriteError.MultipleImportMapElements);

        var bodyBytes = DigestInput(FindImportMap(html)!.Body);

        var builder = new StringBuilder(html.Length);
        var hashes = new List<string>();
        var position = 0;

        while (true)
        {
            var found = html.IndexOf(placeholder, position, StringComparison.Ordinal);
            if (found < 0) break;

            var algorithm = ReadAlgorithmPrefix(html, found);
            // A bare "{importmap}" is not a valid CSP source expression, so a caller that wrote one
            // is told about it rather than handed a policy that silently fails to parse.
            if (algorithm is null) return RewriteResult.Failed(RewriteError.MissingAlgorithmPrefix);

            var hash = algorithm + "-" + ComputeDigest(algorithm, bodyBytes);
            hashes.Add(hash);

            builder.Append(html, position, found - position - algorithm.Length - 1).Append(hash);
            position = found + placeholder.Length;
        }

        builder.Append(html, position, html.Length - position);
        return RewriteResult.Rewritten(builder.ToString(), hashes);
    }

    public static bool ContainsPlaceholder(string html, string placeholder = DefaultPlaceholder) =>
        html != null && html.IndexOf(placeholder, StringComparison.Ordinal) >= 0;

    /// <summary>
    /// Returns the digest a browser computes for the given import map content, such as "sha256-...".
    /// </summary>
    public static string DigestOf(string importMapBody, string algorithm = "sha256") =>
        algorithm + "-" + ComputeDigest(algorithm, DigestInput(importMapBody));

    /// <summary>
    /// The bytes a browser hashes for a Content Security Policy: the text content of the element as
    /// the HTML parser produced it, encoded as UTF-8.
    /// <para>
    /// The parser normalizes the newlines of the input stream before it tokenizes anything, so a
    /// document saved with CRLF still yields a script body with LF. Hashing the bytes on disk would
    /// give a digest that no browser ever computes.
    /// </para>
    /// </summary>
    private static byte[] DigestInput(string importMapBody) =>
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(NormalizeNewlines(importMapBody));

    /// <summary>
    /// Turns every CRLF and every lone CR into an LF, the way the HTML input stream preprocessor
    /// does before the document is tokenized.
    /// </summary>
    internal static string NormalizeNewlines(string text)
    {
        if (text.IndexOf('\r') < 0) return text;

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\r') { builder.Append(text[i]); continue; }
            builder.Append('\n');
            if (i + 1 < text.Length && text[i + 1] == '\n') i++;
        }
        return builder.ToString();
    }

    /// <summary>
    /// Returns the hash algorithm named immediately before the placeholder at
    /// <paramref name="placeholderIndex"/>, or <c>null</c> when there is no such name.
    /// </summary>
    private static string? ReadAlgorithmPrefix(string html, int placeholderIndex)
    {
        foreach (var algorithm in _Algorithms)
        {
            var start = placeholderIndex - algorithm.Length - 1;
            if (start < 0) continue;
            if (html[placeholderIndex - 1] != '-') continue;
            if (string.Compare(html, start, algorithm, 0, algorithm.Length, StringComparison.OrdinalIgnoreCase) == 0) return algorithm;
        }
        return null;
    }

    private static string ComputeDigest(string algorithm, byte[] content)
    {
        using (HashAlgorithm hash = CreateHashAlgorithm(algorithm))
        {
            return Convert.ToBase64String(hash.ComputeHash(content));
        }
    }

    private static HashAlgorithm CreateHashAlgorithm(string algorithm)
    {
        switch (algorithm)
        {
            case "sha384": return SHA384.Create();
            case "sha512": return SHA512.Create();
            default: return SHA256.Create();
        }
    }
}

/// <summary>The content of an import map element and where it sits in the document.</summary>
public sealed class ImportMapElement
{
    public string Body { get; }

    public int BodyStart { get; }

    public int BodyLength { get; }

    internal ImportMapElement(string body, int bodyStart, int bodyLength)
    {
        this.Body = body;
        this.BodyStart = bodyStart;
        this.BodyLength = bodyLength;
    }
}

public enum RewriteError
{
    None,

    /// <summary>The document asks for a digest but has no import map element.</summary>
    NoImportMapElement,

    /// <summary>The document has more than one import map element.</summary>
    MultipleImportMapElements,

    /// <summary>The placeholder is not preceded by "sha256-", "sha384-" or "sha512-".</summary>
    MissingAlgorithmPrefix
}

public sealed class RewriteResult
{
    /// <summary>The rewritten document, or <c>null</c> when nothing was rewritten.</summary>
    public string? Html { get; }

    public bool Changed { get; }

    /// <summary>The digests that were written into the document, such as "sha256-...".</summary>
    public IReadOnlyList<string> Hashes { get; }

    public RewriteError Error { get; }

    public bool Succeeded => this.Error == RewriteError.None;

    private RewriteResult(string? html, bool changed, IReadOnlyList<string> hashes, RewriteError error)
    {
        this.Html = html;
        this.Changed = changed;
        this.Hashes = hashes;
        this.Error = error;
    }

    internal static RewriteResult NotApplicable() => new RewriteResult(null, false, new string[0], RewriteError.None);

    internal static RewriteResult Rewritten(string html, IReadOnlyList<string> hashes) => new RewriteResult(html, true, hashes, RewriteError.None);

    internal static RewriteResult Failed(RewriteError error) => new RewriteResult(null, false, new string[0], error);
}
