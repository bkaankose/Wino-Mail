using System.Security.Cryptography;
using System.Text;

namespace Wino.Editor;

public static class EditorDocumentAssets
{

    private const string ResourcePrefix = "Wino.Editor.Assets.Editor.";
    private const string CharsetMetaTag = "<meta charset=\"utf-8\">";
    private const string LightReaderDocumentTag =
        "<html id=\"wino-document\" lang=\"en\" data-theme=\"light\">";
    private const string DarkReaderDocumentTag =
        "<html id=\"wino-document\" lang=\"en\" data-theme=\"dark\">";

    // Only scripts carrying this per-process nonce can run. Mail markup can never obtain it,
    // so inline event handlers, javascript: URLs and injected <script> elements stay inert
    // even if a sanitizer bypass lets one through. Styles stay inline because mail relies on them.
    private static readonly string ScriptNonce =
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));

    // The reader only loads images and fonts. Remote requests are further filtered per
    // message by WinoMailRenderer, which blocks them when remote content is turned off.
    public static readonly string ReaderContentSecurityPolicy =
        $"default-src 'none'; script-src 'nonce-{ScriptNonce}'; style-src 'unsafe-inline'; " +
        "img-src http: https: data: cid:; font-src http: https: data:; " +
        "base-uri 'none'; form-action 'none'; frame-src 'none'; object-src 'none'";

    public static readonly string EditorContentSecurityPolicy =
        $"default-src 'none'; script-src 'nonce-{ScriptNonce}'; style-src 'unsafe-inline'; " +
        "img-src http: https: data: blob: cid:; font-src data:; " +
        "base-uri 'none'; form-action 'none'; frame-src 'none'; object-src 'none'";

    private static readonly string[] PackagedScriptFileNames =
    [
        "mail-colors.js",
        "editor.js",
        "editor-images.js",
        "editor-tables.js",
        "linkify.min.js",
        "linkify-element.min.js",
        "dompurify-3.4.14.min.js",
        "readability-0.6.0.js",
        "reader.js"
    ];

    private static readonly Lazy<Task<string>> EditorDocument =
        new(() => BuildDocumentAsync(
            "editor.html",
            "editor.css",
            EditorContentSecurityPolicy,
            "dompurify-3.4.14.min.js",
            "mail-colors.js",
            "linkify.min.js",
            "linkify-element.min.js",
            "editor.js",
            "editor-images.js",
            "editor-tables.js"));

    private static readonly Lazy<Task<string>> ReaderDocument =
        new(() => BuildDocumentAsync(
            "reader.html",
            "reader.css",
            ReaderContentSecurityPolicy,
            "mail-colors.js",
            "linkify.min.js",
            "linkify-element.min.js",
            "dompurify-3.4.14.min.js",
            "readability-0.6.0.js",
            "reader.js"));

    public static Task<string> GetEditorDocumentAsync() => EditorDocument.Value;

    public static async Task<string> GetReaderDocumentAsync(bool isDarkMode)
    {
        string document = await ReaderDocument.Value.ConfigureAwait(false);
        return isDarkMode
            ? document.Replace(LightReaderDocumentTag, DarkReaderDocumentTag, StringComparison.Ordinal)
            : document;
    }

    internal static async Task<string> BuildDocumentAsync(
        string pageFileName,
        string stylesheetFileName,
        string contentSecurityPolicy,
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

        // The page's own bootstrap block is the only <script> tag in the template itself.
        html = html.Replace("<script>", NonceScriptTag, StringComparison.Ordinal);

        if (!html.Contains(CharsetMetaTag, StringComparison.Ordinal))
            throw new InvalidOperationException($"'{pageFileName}' must declare {CharsetMetaTag}.");

        html = html.Replace(
            CharsetMetaTag,
            $"{CharsetMetaTag}\n    <meta http-equiv=\"Content-Security-Policy\" content=\"{contentSecurityPolicy}\">",
            StringComparison.Ordinal);

        var inlineScripts = new StringBuilder();
        foreach (string scriptFileName in scriptFileNames)
        {
            string script = await ReadEmbeddedTextAsync(scriptFileName).ConfigureAwait(false);
            inlineScripts.Append(NonceScriptTag)
                .Append(EscapeInlineScript(script))
                .AppendLine("</script>");
        }

        string document = html.Replace(
            "</body>",
            inlineScripts.Append("</body>").ToString(),
            StringComparison.OrdinalIgnoreCase);

        return document;
    }

    private static string NonceScriptTag => $"<script nonce=\"{ScriptNonce}\">";

    internal static string ProcessScriptNonce => ScriptNonce;

    private static async Task<string> ReadEmbeddedTextAsync(string fileName)
    {
        string resourceName = ResourcePrefix + fileName;
        await using Stream stream = typeof(EditorDocumentAssets).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded editor resource '{resourceName}' was not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    private static string EscapeInlineScript(string script) =>
        script.Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase);

    private static string EscapeInlineStyle(string stylesheet) =>
        stylesheet.Replace("</style", "<\\/style", StringComparison.OrdinalIgnoreCase);
}
