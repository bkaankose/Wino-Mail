using System.Text;

namespace Wino.Editor;

internal static class EditorAssetProvider
{
    private const string ResourcePrefix = "Wino.Editor.Assets.Editor.";
    private const string LightReaderDocumentTag =
        "<html id=\"wino-document\" lang=\"en\" data-theme=\"light\">";
    private const string DarkReaderDocumentTag =
        "<html id=\"wino-document\" lang=\"en\" data-theme=\"dark\">";

    private static readonly string[] PackagedScriptFileNames =
    [
        "darkreader.js",
        "editor.js",
        "editor-images.js",
        "editor-tables.js",
        "linkify.min.js",
        "linkify-element.min.js",
        "reader.js"
    ];

    private static readonly Lazy<Task<string>> EditorDocument =
        new(() => BuildDocumentAsync(
            "editor.html",
            "editor.css",
            "darkreader.js",
            "editor.js",
            "editor-images.js",
            "editor-tables.js"));

    private static readonly Lazy<Task<string>> ReaderDocument =
        new(() => BuildDocumentAsync(
            "reader.html",
            "reader.css",
            "darkreader.js",
            "linkify.min.js",
            "linkify-element.min.js",
            "reader.js"));

    public static Task<string> GetEditorDocumentAsync() => EditorDocument.Value;

    public static async Task<string> GetReaderDocumentAsync(bool isDarkMode)
    {
        string document = await ReaderDocument.Value.ConfigureAwait(false);
        return isDarkMode
            ? document.Replace(LightReaderDocumentTag, DarkReaderDocumentTag, StringComparison.Ordinal)
            : document;
    }

    private static async Task<string> BuildDocumentAsync(
        string pageFileName,
        string stylesheetFileName,
        params string[] scriptFileNames)
    {
        string html = await ReadEmbeddedTextAsync(pageFileName).ConfigureAwait(false);
        string stylesheet = await ReadEmbeddedTextAsync(stylesheetFileName).ConfigureAwait(false);
        html = html.Replace(
            $"<link rel=\"stylesheet\" href=\"{stylesheetFileName}\">",
            $"<style>{EscapeInlineStyle(stylesheet)}</style>",
            StringComparison.Ordinal);

        foreach (string scriptFileName in PackagedScriptFileNames)
        {
            html = html.Replace(
                $"<script defer src=\"{scriptFileName}\"></script>",
                string.Empty,
                StringComparison.Ordinal);
        }

        var inlineScripts = new StringBuilder();
        foreach (string scriptFileName in scriptFileNames)
        {
            string script = await ReadEmbeddedTextAsync(scriptFileName).ConfigureAwait(false);
            inlineScripts.Append("<script>")
                .Append(EscapeInlineScript(script))
                .AppendLine("</script>");
        }

        return html.Replace(
            "</body>",
            inlineScripts.Append("</body>").ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ReadEmbeddedTextAsync(string fileName)
    {
        string resourceName = ResourcePrefix + fileName;
        await using Stream stream = typeof(EditorAssetProvider).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded editor resource '{resourceName}' was not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    private static string EscapeInlineScript(string script) =>
        script.Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase);

    private static string EscapeInlineStyle(string stylesheet) =>
        stylesheet.Replace("</style", "<\\/style", StringComparison.OrdinalIgnoreCase);
}
