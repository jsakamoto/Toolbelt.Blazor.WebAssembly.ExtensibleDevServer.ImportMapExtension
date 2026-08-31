using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

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
/// Drives a headless Chromium over the DevTools protocol.
/// <para>
/// A digest that a test computes the same way the code under test computes it proves nothing about
/// whether a browser accepts it. Only a browser can answer that, so this asks one.
/// </para>
/// </summary>
internal static class HeadlessBrowser
{
    /// <summary>How long the WebAssembly runtime is given to download, start and render.</summary>
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(25);

    /// <summary>
    /// Returns the path of a Chromium to drive, or <c>null</c> when this machine has none. Set
    /// "CSP_TEST_BROWSER" to point at a specific one.
    /// </summary>
    public static string? Find()
    {
        var configured = Environment.GetEnvironmentVariable("CSP_TEST_BROWSER");
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured)) return configured;

        string[] candidates = OperatingSystem.IsWindows()
            ?
            [
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            ]
            :
            [
                "/usr/bin/google-chrome", "/usr/bin/google-chrome-stable",
                "/usr/bin/chromium", "/usr/bin/chromium-browser", "/usr/bin/microsoft-edge",
            ];

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Skips the calling test when this machine has no Chromium to drive.</summary>
    public static string Require()
    {
        var path = Find();
        if (path is null) Assert.Ignore("No Chromium was found on this machine. Set \"CSP_TEST_BROWSER\" to the path of one to run this test.");
        return path!;
    }

    public static async Task<PageReport> LoadAsync(string browserPath, string url)
    {
        var port = GetFreePort();
        var profileDir = Path.Combine(Path.GetTempPath(), "ImportMapExtension.Test", "browser-" + Guid.NewGuid().ToString("N"));

        using var browser = new Process
        {
            StartInfo = new ProcessStartInfo(browserPath,
            [
                "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check",
                "--disable-extensions", "--disable-background-networking",
                // Without these two the browser never starts on a continuous integration machine.
                // Its sandbox needs unprivileged user namespaces, which the AppArmor policy of a
                // current Ubuntu denies to a browser outside its packaged location, and the "/dev/shm"
                // of a container is too small for the shared memory it wants. Neither matters for a
                // browser that is started to look at one local page and is killed afterwards.
                "--no-sandbox", "--disable-dev-shm-usage",
                $"--user-data-dir={profileDir}", $"--remote-debugging-port={port}", "about:blank",
            ])
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true }
        };

        // Both streams are drained as they arrive. A redirected stream that nobody reads fills its
        // pipe and blocks the browser, and what it wrote is the only explanation of a browser that
        // never came up.
        var diagnostics = new StringBuilder();
        void Collect(object _, DataReceivedEventArgs e) { if (e.Data is not null) { lock (diagnostics) diagnostics.AppendLine(e.Data); } }
        browser.OutputDataReceived += Collect;
        browser.ErrorDataReceived += Collect;

        if (!browser.Start()) throw new InvalidOperationException($"Could not start \"{browserPath}\".");
        browser.BeginOutputReadLine();
        browser.BeginErrorReadLine();

        try
        {
            await using var session = await DevToolsSession.ConnectAsync(port, browser, diagnostics);

            await session.SendAsync("Log.enable");
            await session.SendAsync("Runtime.enable");
            await session.SendAsync("Page.enable");
            await session.SendAsync("Page.navigate", new { url });

            await Task.Delay(SettleTime);

            var greeting = await session.EvaluateStringAsync("document.querySelector(\"#greeting\")?.textContent ?? \"(no element)\"");

            // The import is evaluated directly rather than triggered through the page, because
            // Blazor renders its own exceptions into the error UI and the failure would not reach
            // the console where this test looks for it.
            var importResult = await session.EvaluateStringAsync(
                "import('./App.razor.js').then(m => 'IMPORT OK: ' + m.greeting()).catch(e => 'IMPORT FAILED: ' + e.message)",
                awaitPromise: true);

            return new PageReport(greeting, importResult, session.ConsoleErrors, session.CspViolations);
        }
        finally
        {
            try { browser.Kill(entireProcessTree: true); } catch { /* it may already be gone */ }
            try { Directory.Delete(profileDir, recursive: true); } catch { /* it is only a temp folder */ }
        }
    }

    public static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// One DevTools connection. A single loop owns the receive side: it completes the answers to
    /// commands and collects the events, so callers never race each other for the socket.
    /// </summary>
    private sealed class DevToolsSession : IAsyncDisposable
    {
        private readonly ClientWebSocket _Socket;
        private readonly Task _Reading;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _Pending = [];
        private readonly List<string> _ConsoleErrors = [];
        private readonly List<string> _CspViolations = [];
        private int _NextId;

        public IReadOnlyList<string> ConsoleErrors { get { lock (this._ConsoleErrors) return [.. this._ConsoleErrors]; } }

        public IReadOnlyList<string> CspViolations { get { lock (this._CspViolations) return [.. this._CspViolations]; } }

        private DevToolsSession(ClientWebSocket socket)
        {
            this._Socket = socket;
            this._Reading = this.ReadAsync();
        }

        public static async Task<DevToolsSession> ConnectAsync(int port, Process browser, StringBuilder diagnostics)
        {
            var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(await FindPageTargetAsync(port, browser, diagnostics)), CancellationToken.None);
            return new DevToolsSession(socket);
        }

        public async Task<JsonElement> SendAsync(string method, object? parameters = null)
        {
            var id = Interlocked.Increment(ref this._NextId);
            var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            this._Pending[id] = answer;

            var payload = JsonSerializer.Serialize(new { id, method, @params = parameters ?? new { } });
            await this._Socket.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, CancellationToken.None);

            var completed = await Task.WhenAny(answer.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            if (completed != answer.Task) throw new TimeoutException($"The browser did not answer \"{method}\".");
            return await answer.Task;
        }

        public async Task<string> EvaluateStringAsync(string expression, bool awaitPromise = false)
        {
            var answer = await this.SendAsync("Runtime.evaluate", new { expression, awaitPromise, returnByValue = true });
            return answer.TryGetProperty("result", out var result) && result.TryGetProperty("value", out var value)
                ? value.GetString() ?? ""
                : "";
        }

        private async Task ReadAsync()
        {
            var buffer = new byte[64 * 1024];
            var message = new StringBuilder();

            while (this._Socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult received;
                try { received = await this._Socket.ReceiveAsync(buffer, CancellationToken.None); }
                catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException) { return; }

                if (received.MessageType == WebSocketMessageType.Close) return;

                message.Append(Encoding.UTF8.GetString(buffer, 0, received.Count));
                if (!received.EndOfMessage) continue;

                try { this.Dispatch(message.ToString()); } catch (JsonException) { /* not something this test needs */ }
                message.Clear();
            }
        }

        private void Dispatch(string json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("id", out var id) && this._Pending.TryRemove(id.GetInt32(), out var answer))
            {
                answer.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
                return;
            }

            if (!root.TryGetProperty("method", out var method)) return;

            switch (method.GetString())
            {
                case "Log.entryAdded":
                    var entry = root.GetProperty("params").GetProperty("entry");
                    var text = entry.GetProperty("text").GetString() ?? "";
                    var source = entry.GetProperty("source").GetString() ?? "";
                    if (entry.GetProperty("level").GetString() == "error") { lock (this._ConsoleErrors) this._ConsoleErrors.Add($"{source}: {text}"); }
                    if (text.Contains("Content Security Policy", StringComparison.OrdinalIgnoreCase)) { lock (this._CspViolations) this._CspViolations.Add(text); }
                    break;

                case "Runtime.exceptionThrown":
                    var details = root.GetProperty("params").GetProperty("exceptionDetails");
                    lock (this._ConsoleErrors) this._ConsoleErrors.Add(details.GetProperty("text").GetString() ?? "an exception was thrown");
                    break;
            }
        }

        private static async Task<string> FindPageTargetAsync(int port, Process browser, StringBuilder diagnostics)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

            for (var attempt = 0; attempt < 150; attempt++)
            {
                // A browser that refused to start is the common case, and waiting the full thirty
                // seconds for it hides the reason it gives on the way out.
                if (browser.HasExited)
                {
                    throw new InvalidOperationException(
                        $"The browser exited with code {browser.ExitCode} before it exposed a debugging target.{Explain(diagnostics)}");
                }

                try
                {
                    using var targets = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list"));
                    foreach (var target in targets.RootElement.EnumerateArray())
                    {
                        if (target.GetProperty("type").GetString() == "page") return target.GetProperty("webSocketDebuggerUrl").GetString()!;
                    }
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { /* it is not listening yet */ }

                await Task.Delay(200);
            }
            throw new InvalidOperationException($"The browser never exposed a debugging target.{Explain(diagnostics)}");
        }

        private static string Explain(StringBuilder diagnostics)
        {
            lock (diagnostics)
            {
                return diagnostics.Length == 0 ? " It said nothing about why." : "\nThe browser said:\n" + diagnostics;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { await this._Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); }
            catch (Exception e) when (e is WebSocketException or ObjectDisposedException) { /* the browser is going away anyway */ }

            await this._Reading;
            this._Socket.Dispose();
        }
    }
}
