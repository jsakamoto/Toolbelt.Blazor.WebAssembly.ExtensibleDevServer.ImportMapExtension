using System.Collections.Generic;
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Toolbelt.Blazor.WebAssembly.ExtensibleDevServer.ImportMapExtension;

/// <summary>
/// Replaces the Content Security Policy placeholder in each of the given HTML documents with the
/// digest of the import map that the .NET SDK embedded into that same document.
/// </summary>
public class RewriteImportMapCspHash : Task
{
    /// <summary>
    /// The HTML documents to rewrite. These are the intermediate files the SDK produced, not the
    /// files in "wwwroot", so that everything the SDK computes afterwards - the pre-compressed
    /// copies, the integrity values, the endpoint manifest, the service worker asset manifest - is
    /// computed from the rewritten content.
    /// </summary>
    [Required]
    public ITaskItem[] HtmlFiles { get; set; } = new ITaskItem[0];

    [Required]
    public string Placeholder { get; set; } = ImportMapCspRewriter.DefaultPlaceholder;

    /// <summary>The documents that were actually changed.</summary>
    [Output]
    public ITaskItem[] RewrittenFiles { get; private set; } = new ITaskItem[0];

    /// <summary>
    /// The digests that were written, such as "sha256-...", for callers that deliver the policy as
    /// a response header instead of a "meta" element.
    /// </summary>
    [Output]
    public string[] Hashes { get; private set; } = new string[0];

    public override bool Execute()
    {
        var rewritten = new List<ITaskItem>();
        var hashes = new List<string>();

        foreach (var item in this.HtmlFiles)
        {
            var path = item.GetMetadata("FullPath");
            if (!File.Exists(path)) continue;

            var content = HtmlFile.Read(path);
            var result = ImportMapCspRewriter.Rewrite(content.Text, this.Placeholder);

            if (!result.Succeeded) { this.LogFailure(result.Error, path); return false; }
            if (!result.Changed) continue;

            HtmlFile.Write(path, content.With(result.Html!));

            rewritten.Add(item);
            foreach (var hash in result.Hashes) { if (!hashes.Contains(hash)) hashes.Add(hash); }

            this.Log.LogMessage(MessageImportance.Normal,
                $"Content Security Policy digest of the import map in \"{item.GetMetadata("RelativePath")}\": {string.Join(", ", result.Hashes)}");
        }

        this.RewrittenFiles = rewritten.ToArray();
        this.Hashes = hashes.ToArray();
        return !this.Log.HasLoggedErrors;
    }

    private void LogFailure(RewriteError error, string path)
    {
        string code;
        string message;

        switch (error)
        {
            case RewriteError.NoImportMapElement:
                code = "IMCSP010";
                message = $"\"{path}\" contains the \"{this.Placeholder}\" placeholder, but it has no import map. " +
                          "Add \"<script type=\\\"importmap\\\"></script>\" to the \"<head>\" content of your \"wwwroot/index.html\".";
                break;

            case RewriteError.MultipleImportMapElements:
                code = "IMCSP011";
                message = $"\"{path}\" has more than one import map, so there is no single digest to write into the " +
                          "Content Security Policy. Leave exactly one \"<script type=\\\"importmap\\\">\" element in the document.";
                break;

            case RewriteError.MissingAlgorithmPrefix:
                code = "IMCSP012";
                message = $"\"{path}\" uses the \"{this.Placeholder}\" placeholder without a hash algorithm in front of it. " +
                          $"Write it as \"'sha256-{this.Placeholder}'\" (\"sha384\" and \"sha512\" also work).";
                break;

            default:
                code = "IMCSP019";
                message = $"\"{path}\" could not be rewritten.";
                break;
        }

        this.Log.LogError(subcategory: null, errorCode: code, helpKeyword: null, file: path,
            lineNumber: 0, columnNumber: 0, endLineNumber: 0, endColumnNumber: 0, message: message);
    }
}
