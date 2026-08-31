using System.IO;
using System.Text;

namespace Toolbelt.Blazor.WebAssembly.ExtensibleDevServer.ImportMapExtension;

/// <summary>
/// Reads and writes HTML documents while keeping the byte order mark exactly as it was found.
/// </summary>
internal static class HtmlFile
{
    private static readonly byte[] _Utf8Bom = { 0xEF, 0xBB, 0xBF };

    public static HtmlContent Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var hasBom = bytes.Length >= 3 && bytes[0] == _Utf8Bom[0] && bytes[1] == _Utf8Bom[1] && bytes[2] == _Utf8Bom[2];
        var offset = hasBom ? _Utf8Bom.Length : 0;
        return new HtmlContent(Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset), hasBom);
    }

    public static void Write(string path, HtmlContent content)
    {
        File.WriteAllText(path, content.Text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: content.HasByteOrderMark));
    }
}

internal readonly struct HtmlContent
{
    public string Text { get; }

    public bool HasByteOrderMark { get; }

    public HtmlContent(string text, bool hasByteOrderMark)
    {
        this.Text = text;
        this.HasByteOrderMark = hasByteOrderMark;
    }

    public HtmlContent With(string text) => new HtmlContent(text, this.HasByteOrderMark);
}
