using Microsoft.Playwright;

namespace ImportMapExtension.Test.Internals;

/// <summary>
/// What one page load produced: what it rendered, what a direct module import did, and everything
/// the browser complained about.
/// </summary>
internal sealed record PageReport(string Greeting, string ImportResult, IReadOnlyList<string> ConsoleErrors, IReadOnlyList<string> CspViolations)
{
    public string Describe() =>
        $"greeting: \"{this.Greeting}\"\n" +
        $"import: \"{this.ImportResult}\"\n" +
        $"CSP violations:\n  {string.Join("\n  ", this.CspViolations.DefaultIfEmpty("(none)"))}\n" +
        $"console errors:\n  {string.Join("\n  ", this.ConsoleErrors.DefaultIfEmpty("(none)"))}";
}

/// <summary>
/// Drives a headless Chromium with Playwright.
/// <para>
/// A digest that a test computes the same way the code under test computes it proves nothing about
/// whether a browser accepts it. Only a browser can answer that, so this asks one. Playwright brings
/// a Chromium of its own, so this needs no browser to be installed on the machine running the tests
/// and none inside the container that the app under test is running in.
/// </para>
/// </summary>
internal static class HeadlessBrowser
{
    /// <summary>How long the WebAssembly runtime is given to download, start and render.</summary>
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(30);

    /// <summary>What the sample app renders until its module has been imported.</summary>
    private const string NotLoadedYet = "(not loaded)";

    /// <summary>
    /// Downloads the Chromium that goes with the version of Playwright referenced here. It lands in
    /// a cache of Playwright's own, so this downloads nothing on a machine that already has it, and
    /// a browser the machine happens to have installed is neither used nor needed.
    /// </summary>
    public static void InstallChromium()
    {
        var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
        exitCode.Is(0, message: "\"playwright install chromium\" failed.");
    }

    public static async Task<PageReport> LoadAsync(string url)
    {
        using var playwright = await Playwright.CreateAsync();

        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            // Without these two the browser never starts on a continuous integration machine. Its
            // sandbox needs unprivileged user namespaces, which the AppArmor policy of a current
            // Ubuntu denies to a browser outside its packaged location, and the "/dev/shm" of a
            // container is too small for the shared memory it wants. Neither matters for a browser
            // that is started to look at one local page and is thrown away afterwards.
            Args = ["--no-sandbox", "--disable-dev-shm-usage"],
        });

        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();

        var consoleErrors = new List<string>();
        var cspViolations = new List<string>();

        // A refused import map and a blocked module are reported by the browser itself rather than
        // by anything running on the page, so neither of them reaches the "console" event. The
        // DevTools log is where they land, and Playwright hands out a session on it for a Chromium.
        var devTools = await context.NewCDPSessionAsync(page);
        devTools.Event("Log.entryAdded").OnEvent += (_, entry) =>
        {
            if (entry is null) return;

            var log = entry.Value.GetProperty("entry");
            var text = log.GetProperty("text").GetString() ?? "";
            var source = log.GetProperty("source").GetString() ?? "";

            if (log.GetProperty("level").GetString() == "error") { lock (consoleErrors) consoleErrors.Add($"{source}: {text}"); }
            if (text.Contains("Content Security Policy", StringComparison.OrdinalIgnoreCase)) { lock (cspViolations) cspViolations.Add(text); }
        };
        await devTools.SendAsync("Log.enable");

        page.PageError += (_, error) => { lock (consoleErrors) consoleErrors.Add(error); };

        await page.GotoAsync(url);

        // Wait for the app to render what its module returns. A page that never gets that far is
        // exactly what some of these tests are looking for, so running out of time here is not a
        // failure: whatever the page ends up saying is what gets reported.
        try
        {
            await page.WaitForFunctionAsync(
                $$"""() => { const el = document.querySelector("#greeting"); return el !== null && el.textContent !== "{{NotLoadedYet}}"; }""",
                arg: null,
                new PageWaitForFunctionOptions { Timeout = (float)SettleTime.TotalMilliseconds });
        }
        catch (Exception e) when (e is TimeoutException or PlaywrightException) { /* the report below says what actually happened */ }

        var greeting = await page.EvaluateAsync<string>(
            """() => document.querySelector("#greeting")?.textContent ?? "(no element)" """);

        // The import is evaluated directly rather than triggered through the page, because Blazor
        // renders its own exceptions into the error UI and the failure would not reach the log
        // where this test looks for it.
        var importResult = await page.EvaluateAsync<string>(
            """() => import("./App.razor.js").then(m => "IMPORT OK: " + m.greeting()).catch(e => "IMPORT FAILED: " + e.message)""");

        // The browser reports what it refused as it happens, and the import above is what provokes
        // some of those reports, so give the last of them a moment to arrive.
        await Task.Delay(TimeSpan.FromSeconds(1));

        lock (consoleErrors)
        {
            lock (cspViolations) return new PageReport(greeting, importResult, [.. consoleErrors], [.. cspViolations]);
        }
    }
}
