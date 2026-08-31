using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ImportMapExtension.Test.Internals;

/// <summary>
/// Computes what a browser would compute, written out here rather than borrowed from the code under
/// test. Calling the implementation to check the implementation would prove nothing.
/// </summary>
internal static partial class Digest
{
    [GeneratedRegex(@"(?is)<script[^>]*type=""importmap""[^>]*>(.*?)</script>")]
    private static partial Regex ImportMapElement { get; }

    [GeneratedRegex(@"'(sha256-[A-Za-z0-9+/=]+)'")]
    private static partial Regex PolicyDigest { get; }

    public static string ImportMapOf(string html)
    {
        var body = ImportMapElement.Match(html).Groups[1].Value;
        body.IsNot("", message: "The document has no import map in it.");
        return body;
    }

    /// <summary>The digest a browser computes for the import map of the given document.</summary>
    public static string OfImportMapIn(string html)
    {
        // What the HTML parser hands to the policy check has LF newlines, whatever the file uses.
        var body = ImportMapOf(html).Replace("\r\n", "\n").Replace("\r", "\n");

        using var sha256 = SHA256.Create();
        return "sha256-" + Convert.ToBase64String(sha256.ComputeHash(new UTF8Encoding(false).GetBytes(body)));
    }

    /// <summary>The digest written into the policy of the given document.</summary>
    public static string InPolicyOf(string html)
    {
        var digest = PolicyDigest.Match(html).Groups[1].Value;
        digest.IsNot("", message: "The policy has no digest in it.");
        return digest;
    }
}
