using System.Text;
namespace Wino.Editor;
internal static class EditorAssetProvider
{
    internal static string ReaderContentSecurityPolicy => EditorDocumentAssets.ReaderContentSecurityPolicy;
    internal static string EditorContentSecurityPolicy => EditorDocumentAssets.EditorContentSecurityPolicy;
    public static async Task<string> GetEditorDocumentAsync() => ValidateDocument(await EditorDocumentAssets.GetEditorDocumentAsync().ConfigureAwait(false));
    public static async Task<string> GetReaderDocumentAsync(bool isDarkMode) => ValidateDocument(await EditorDocumentAssets.GetReaderDocumentAsync(isDarkMode).ConfigureAwait(false));
    internal static string BindSession(string document, string sessionId)
    {
        int scriptStart = document.IndexOf("<script nonce=", StringComparison.Ordinal);
        if (scriptStart < 0) throw new InvalidOperationException("Document has no authorized bootstrap script.");
        int bodyStart = document.IndexOf('>', scriptStart) + 1;
        if (bodyStart <= 0) throw new InvalidOperationException("Document has no authorized bootstrap script.");
        return ValidateDocument(document.Insert(bodyStart, $"window.winoSessionId = '{sessionId}';"));
    }

    private static string ValidateDocument(string document)
    {
        const int limit = 2 * 1024 * 1024;
        int size = Encoding.UTF8.GetByteCount(document);
        if (size >= limit) throw new InvalidOperationException($"The assembled editor document is {size} bytes, which reaches WebView2's {limit}-byte NavigateToString limit.");
        return document;
    }
}
