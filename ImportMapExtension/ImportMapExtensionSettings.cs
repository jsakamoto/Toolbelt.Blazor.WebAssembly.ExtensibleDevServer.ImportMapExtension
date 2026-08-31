namespace Toolbelt.Blazor.WebAssembly.ExtensibleDevServer.ImportMapExtension;

/// <summary>
/// What the project being served asked for. The development server runs in a process of its own, so the only
/// way it learns any of this is through the environment variables that this package's MSBuild
/// targets put into the development server's response file.
/// </summary>
internal sealed record ImportMapExtensionSettings(string Placeholder, bool StripIntegrity)
{
    public const string PlaceholderVariable = "IMPORTMAP_CSP_PLACEHOLDER";

    public const string StripIntegrityVariable = "IMPORTMAP_STRIP_INTEGRITY";

    public static ImportMapExtensionSettings FromEnvironment()
    {
        var placeholder = Environment.GetEnvironmentVariable(PlaceholderVariable);
        if (string.IsNullOrWhiteSpace(placeholder)) placeholder = ImportMapCspRewriter.DefaultPlaceholder;

        // Anything other than an explicit "false" leaves the removal on, so a project that never
        // set the variable gets the behavior this package exists to provide.
        var stripIntegrity = !string.Equals(
            Environment.GetEnvironmentVariable(StripIntegrityVariable), "false", StringComparison.OrdinalIgnoreCase);

        return new ImportMapExtensionSettings(placeholder, stripIntegrity);
    }
}
