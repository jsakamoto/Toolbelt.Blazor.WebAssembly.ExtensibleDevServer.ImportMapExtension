using System.Collections.Generic;
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Toolbelt.Blazor.WebAssembly.ExtensibleDevServer.ImportMapExtension;

/// <summary>
/// Reports which of the given HTML documents carry the Content Security Policy placeholder.
/// <para>
/// The build uses this twice. Before anything else, to find out whether this package has any work
/// to do for the project being built. And at the very end, to make sure that the work was done - a
/// placeholder that survives into the published output would leave an app that no browser can
/// start, so it has to fail the build instead.
/// </para>
/// </summary>
public class FindImportMapCspPlaceholder : Task
{
    [Required]
    public ITaskItem[] Files { get; set; } = new ITaskItem[0];

    [Required]
    public string Placeholder { get; set; } = ImportMapCspRewriter.DefaultPlaceholder;

    [Output]
    public ITaskItem[] MatchedFiles { get; private set; } = new ITaskItem[0];

    public override bool Execute()
    {
        var matched = new List<ITaskItem>();

        foreach (var item in this.Files)
        {
            var path = item.GetMetadata("FullPath");
            if (!File.Exists(path)) continue;
            if (ImportMapCspRewriter.ContainsPlaceholder(HtmlFile.Read(path).Text, this.Placeholder)) matched.Add(item);
        }

        this.MatchedFiles = matched.ToArray();
        return true;
    }
}
