namespace Toolbelt.Blazor.WebAssembly.ExtensibleDevServer.ImportMapExtension;

/// <summary>
/// What this extension does to one HTML document. Kept apart from the middleware so that it can be
/// exercised without an HTTP pipeline.
/// </summary>
internal sealed class ImportMapHtmlProcessor(string placeholder, bool stripIntegrity)
{
    /// <summary>
    /// Removes the "integrity" member of the import map, then writes the digest of what is left
    /// into the Content Security Policy.
    /// <para>
    /// The order matters. Removing the member changes the content of the import map, which changes
    /// the digest a browser computes for it, so the policy has to be written from the content that
    /// actually ships. Doing these in two passes, or in the other order, produces a policy that
    /// blocks the very import map it is meant to allow.
    /// </para>
    /// </summary>
    /// <returns>The rewritten document, or <c>null</c> when there was nothing to change.</returns>
    public string? Process(string html)
    {
        var element = ImportMapCspRewriter.FindImportMap(html);
        if (element is null) return null;

        var changed = false;

        if (stripIntegrity)
        {
            var withoutIntegrity = ImportMapJson.RemoveIntegrity(element.Body);
            if (withoutIntegrity is not null)
            {
                html = ImportMapCspRewriter.ReplaceImportMapBody(html, withoutIntegrity);
                changed = true;
            }
        }

        // A document with no placeholder is left as it is. Its author either writes the policy some
        // other way or has none at all, and neither is this extension's business.
        var result = ImportMapCspRewriter.Rewrite(html, placeholder);
        if (result.Succeeded && result.Changed) return result.Html;

        return changed ? html : null;
    }
}
