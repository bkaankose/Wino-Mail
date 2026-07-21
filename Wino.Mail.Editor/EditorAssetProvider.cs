using System.Text;
using Windows.Storage;

namespace Wino.Mail.Editor;

internal static class EditorAssetProvider
{
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

    private static readonly string[] PackageAssetRoots =
    [
        "ms-appx:///Wino.Mail.Editor/Assets/Editor/",
        "ms-appx:///Assets/Editor/"
    ];

    private static readonly Lazy<Task<string>> ResolvedPackageAssetRoot =
        new(ResolvePackageAssetRootAsync);

    // Read the packaged files through ms-appx and give either browser one
    // self-contained page. This avoids browser-specific relative-URI loading.
    public static Task<string> GetEditorDocumentAsync() => BuildDocumentAsync(
        "editor.html",
        "editor.css",
        "darkreader.js",
        "editor.js",
        "editor-images.js",
        "editor-tables.js");

    // EdgeHTML does not need the Chromium-oriented DarkReader bundle. Keeping
    // it out of the compatibility document also prevents unsupported syntax in
    // that third-party script from interfering with the editor bootstrap.
    public static Task<string> GetLegacyEditorDocumentAsync() => BuildDocumentAsync(
        "editor.html",
        "editor.css",
        "editor.js",
        "editor-images.js",
        "editor-tables.js");

    public static Task<string> GetReaderWebView2DocumentAsync() => BuildDocumentAsync(
        "reader.html",
        "reader.css",
        "darkreader.js",
        "linkify.min.js",
        "linkify-element.min.js",
        "reader.js");

    public static Task<string> GetLegacyReaderDocumentAsync() => BuildDocumentAsync(
        "reader.html",
        "reader.css",
        "reader.js");

    private static async Task<string> BuildDocumentAsync(
        string pageFileName,
        string stylesheetFileName,
        params string[] scriptFileNames)
    {
        string html = await ReadPackageTextAsync(pageFileName);
        string stylesheet = await ReadPackageTextAsync(stylesheetFileName);
        html = html.Replace(
            $"<link rel=\"stylesheet\" href=\"{stylesheetFileName}\">",
            $"<style>{EscapeInlineStyle(stylesheet)}</style>",
            StringComparison.Ordinal);

        foreach (string packagedScriptFileName in PackagedScriptFileNames)
        {
            html = html.Replace(
                $"<script defer src=\"{packagedScriptFileName}\"></script>",
                string.Empty,
                StringComparison.Ordinal);
        }

        var inlineScripts = new StringBuilder();
        foreach (string scriptFileName in scriptFileNames)
        {
            string script = await ReadPackageTextAsync(scriptFileName);
            inlineScripts.Append("<script>")
                .Append(EscapeInlineScript(script))
                .AppendLine("</script>");
        }

        return html.Replace(
            "</body>",
            inlineScripts.Append("</body>").ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ReadPackageTextAsync(string fileName)
    {
        string packageRoot = await ResolvedPackageAssetRoot.Value;
        StorageFile file = await StorageFile.GetFileFromApplicationUriAsync(
            new Uri(packageRoot + fileName));
        return await FileIO.ReadTextAsync(file);
    }

    private static async Task<string> ResolvePackageAssetRootAsync()
    {
        foreach (string packageRoot in PackageAssetRoots)
        {
            try
            {
                _ = await StorageFile.GetFileFromApplicationUriAsync(
                    new Uri(packageRoot + "editor.html"));
                return packageRoot;
            }
            catch (Exception exception) when (IsMissingPackageFile(exception))
            {
                // Referenced UWP libraries and directly included content can
                // produce different package roots. Try the next valid layout.
            }
        }

        throw new FileNotFoundException(
            $"Wino editor assets were not found in the app package. Tried: {string.Join(", ", PackageAssetRoots)}");
    }

    private static bool IsMissingPackageFile(Exception exception) =>
        exception is FileNotFoundException ||
        exception.HResult == unchecked((int)0x80070002) ||
        exception.HResult == unchecked((int)0x80070003);

    private static string EscapeInlineScript(string script) =>
        script.Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase);

    private static string EscapeInlineStyle(string stylesheet) =>
        stylesheet.Replace("</style", "<\\/style", StringComparison.OrdinalIgnoreCase);
}
