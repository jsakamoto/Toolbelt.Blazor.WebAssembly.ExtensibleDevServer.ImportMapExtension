using ImportMapExtension.Test.Internals;
using Toolbelt;
using Toolbelt.Diagnostics;

namespace ImportMapExtension.Test;

/// <summary>
/// The point of this package, end to end: run the sample app the way a developer does, edit a
/// collocated JavaScript module, reload without rebuilding, and have a real browser accept both the
/// module and the strict policy around the import map.
/// <para>
/// Every test here works on a copy of the solution, so editing the sample app's sources never
/// touches the working tree.
/// </para>
/// </summary>
[Parallelizable(ParallelScope.None)]
public class DevServerE2ETests
{
    private const string OriginalModule = "export const greeting = () => \"Hello, Blazor!\";\n";

    private const string EditedModule = "export const greeting = () => \"Edited without a rebuild!\";\n";

    [Test]
    public async Task EditingAModuleAndReloadingWorks()
    {
        var browser = HeadlessBrowser.Require();

        await using var app = await DevServer.StartAsync();

        // The app works as it stands: it renders, nothing is blocked, nothing is logged.
        var before = await HeadlessBrowser.LoadAsync(browser, app.BaseAddress);
        before.CspViolations.Is([], message: $"The browser blocked something.\n{before.Describe()}");
        before.ConsoleErrors.Is([], message: $"The browser reported an error.\n{before.Describe()}");
        before.Greeting.Is("Hello, Blazor!", message: before.Describe());

        // Edit the module. No rebuild, no restart. This is what breaks without this package.
        await app.WriteModuleAsync(EditedModule);

        var after = await HeadlessBrowser.LoadAsync(browser, app.BaseAddress);
        after.CspViolations.Is([], message: $"The browser blocked something after the edit.\n{after.Describe()}");
        after.ConsoleErrors.Is([], message: $"The browser reported an error after the edit.\n{after.Describe()}");
        after.ImportResult.Is("IMPORT OK: Edited without a rebuild!", message: after.Describe());
        after.Greeting.Is("Edited without a rebuild!", message: after.Describe());
    }

    /// <summary>
    /// Proves the test above is testing something. With the removal turned off, the stale
    /// "integrity" is exactly what blocks the edited module.
    /// </summary>
    [Test]
    public async Task WithoutTheIntegrityRemovalTheEditedModuleIsBlocked()
    {
        var browser = HeadlessBrowser.Require();

        await using var app = await DevServer.StartAsync("-p:ImportMapStripIntegrity=false");
        await app.WriteModuleAsync(EditedModule);

        var report = await HeadlessBrowser.LoadAsync(browser, app.BaseAddress);

        report.ImportResult.StartsWith("IMPORT FAILED").IsTrue(message: $"The module was expected to be blocked.\n{report.Describe()}");
        report.ConsoleErrors.Any(e => e.Contains("integrity", StringComparison.OrdinalIgnoreCase))
            .IsTrue(message: $"The browser was expected to complain about the integrity.\n{report.Describe()}");
    }

    /// <summary>
    /// Proves the other half. With nothing to fill the placeholder in, the policy names a digest
    /// that no import map has, and the browser refuses the import map itself.
    /// </summary>
    [Test]
    public async Task WithoutTheDigestBeingWrittenTheImportMapIsBlocked()
    {
        var browser = HeadlessBrowser.Require();

        // A token the page does not contain leaves "sha256-{importmap}" standing in the policy.
        await using var app = await DevServer.StartAsync("-p:ImportMapCspPlaceholder=@@never@@");

        var report = await HeadlessBrowser.LoadAsync(browser, app.BaseAddress);

        report.CspViolations.IsNot([], message: $"The import map was expected to be blocked.\n{report.Describe()}");
    }

    [Test]
    public async Task EveryRouteThatAnswersWithTheDocumentCarriesAMatchingPolicy()
    {
        await using var app = await DevServer.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.BaseAddress) };

        foreach (var path in new[] { "/", "/index.html", "/some/deep/link" })
        {
            var html = await http.GetStringAsync(path);
            Digest.InPolicyOf(html).Is(Digest.OfImportMapIn(html), message: $"The policy served at \"{path}\" does not match its import map.");
            Digest.ImportMapOf(html).Contains("\"integrity\"").IsFalse(message: $"The import map served at \"{path}\" still has its integrity.");
        }
    }

    /// <summary>
    /// The development server answers with a pre-compressed copy when the request allows it, which the
    /// extension has to turn off for itself or it would have no plain text to rewrite.
    /// </summary>
    [Test]
    public async Task ACompressionAwareClientGetsTheSameRewrittenDocument()
    {
        await using var app = await DevServer.StartAsync();
        using var handler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };
        using var http = new HttpClient(handler) { BaseAddress = new Uri(app.BaseAddress) };

        var html = await http.GetStringAsync("/index.html");

        Digest.InPolicyOf(html).Is(Digest.OfImportMapIn(html));
    }

    /// <summary>The sample app running from a throwaway copy of the solution.</summary>
    private sealed class DevServer : IAsyncDisposable
    {
        private readonly WorkDirectory _Workspace;
        private readonly XProcess _Process;

        public string BaseAddress { get; }

        private string ModulePath => Path.Combine(this._Workspace, "SampleApp", "App.razor.js");

        private DevServer(WorkDirectory workspace, XProcess process, string baseAddress)
        {
            this._Workspace = workspace;
            this._Process = process;
            this.BaseAddress = baseAddress;
        }

        public static async Task<DevServer> StartAsync(string extraArguments = "")
        {
            var workspace = await PackagedSolution.CreateWorkspaceAsync();
            var port = HeadlessBrowser.GetFreePort();
            var baseAddress = $"http://127.0.0.1:{port}/";

            // The development server reads this, and a child process inherits it.
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", baseAddress.TrimEnd('/'));
            try
            {
                var process = XProcess.Start("dotnet",
                    $"run --project \"{Path.Combine(workspace, "SampleApp", "SampleApp.csproj")}\" --no-launch-profile " +
                    $"-p:ImportMapExtensionVersion={PackagedSolution.PackageVersion} {extraArguments} -nodeReuse:false",
                    workspace);

                var listening = await process.WaitForOutputAsync(line => line.Contains("Now listening on"), 300000);
                if (!listening)
                {
                    process.Dispose();
                    workspace.Dispose();
                    Assert.Fail($"The development server never started listening.\n{process.Output}");
                }

                return new DevServer(workspace, process, baseAddress);
            }
            finally
            {
                Environment.SetEnvironmentVariable("ASPNETCORE_URLS", null);
            }
        }

        public async Task WriteModuleAsync(string content)
        {
            await File.WriteAllTextAsync(this.ModulePath, content);
            // Give the file watchers a moment to settle before the page is loaded again.
            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        public ValueTask DisposeAsync()
        {
            this._Process.Dispose();
            this._Workspace.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
