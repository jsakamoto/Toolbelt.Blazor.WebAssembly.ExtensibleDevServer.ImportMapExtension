using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Toolbelt.Blazor.WebAssembly.ExtensibleDevServer.ImportMapExtension;

public class ImportMapExtensionStartupFilter : IStartupFilter
{
    /// <summary>
    /// The request headers that would make the development server answer with something other than
    /// the plain, complete and uncompressed document, which is what this extension has to rewrite.
    /// </summary>
    private static readonly string[] _HeadersToStrip = [
        "Accept-Encoding",      // the server serves a pre-compressed variant when this allows it
        "Range", "If-Range",    // a partial response cannot be rewritten
        "If-None-Match", "If-Modified-Since", "If-Match", "If-Unmodified-Since" // a 304 has no body
    ];

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        var settings = ImportMapExtensionSettings.FromEnvironment();
        var processor = new ImportMapHtmlProcessor(settings.Placeholder, settings.StripIntegrity);

        return app =>
        {
            app.Use((context, nextMiddleware) => RewriteHtmlResponse(context, nextMiddleware, processor));

            next(app);
        };
    }

    /// <summary>
    /// Tells whether a request could plausibly be answered with an HTML document. The response
    /// content type is what actually decides, but the request headers have to be adjusted before
    /// the response exists, so this keeps every other asset out of the buffering path.
    /// </summary>
    internal static bool MightReturnHtml(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method)) return false;

        var path = request.Path.Value;
        if (string.IsNullOrEmpty(path)) return true;

        var lastSegment = path.AsSpan(path.LastIndexOf('/') + 1);
        // No extension means a route that the SPA fallback answers with the document.
        if (lastSegment.IndexOf('.') < 0) return true;
        if (lastSegment.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) return true;

        return request.Headers.Accept.Any(value => value?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static async Task RewriteHtmlResponse(HttpContext context, Func<Task> nextMiddleware, ImportMapHtmlProcessor processor)
    {
        if (!MightReturnHtml(context.Request))
        {
            await nextMiddleware();
            return;
        }

        foreach (var header in _HeadersToStrip) context.Request.Headers.Remove(header);

        // Capture what the server would have sent, so that it can be rewritten before it goes out.
        var originalBody = context.Response.Body;
        using var capturedBody = new MemoryStream();
        context.Response.Body = capturedBody;
        try
        {
            await nextMiddleware();
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        var capturedBytes = capturedBody.ToArray();
        var responseBytes = TryRewrite(context, capturedBytes, processor) ?? capturedBytes;

        if (!ReferenceEquals(responseBytes, capturedBytes))
        {
            // The body is no longer the content of the file on disk, so the validators that
            // identify that file must not be sent along with it.
            context.Response.Headers.Remove("ETag");
            context.Response.Headers.Remove("Last-Modified");
            context.Response.Headers.Remove("Content-Encoding");
        }

        context.Response.ContentLength = responseBytes.Length;
        await originalBody.WriteAsync(responseBytes);
    }

    private static byte[]? TryRewrite(HttpContext context, byte[] capturedBytes, ImportMapHtmlProcessor processor)
    {
        if (context.Response.StatusCode != StatusCodes.Status200OK) return null;

        var contentType = context.Response.ContentType;
        if (contentType is null || !contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase)) return null;

        var rewritten = processor.Process(Encoding.UTF8.GetString(capturedBytes));
        return rewritten is null ? null : new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(rewritten);
    }
}
