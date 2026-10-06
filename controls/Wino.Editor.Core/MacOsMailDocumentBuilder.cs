using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wino.Editor;

/// <summary>Fresh shells for WKWebView. No untrusted mail is included in the shell.</summary>
public static class MacOsMailDocumentBuilder
{
    public static Task<string> BuildReaderAsync(string sessionId, RemoteContentPolicy policy,
        bool darkMode, CancellationToken cancellationToken = default) =>
        BuildAsync(false, sessionId, policy, darkMode, cancellationToken);

    public static Task<string> BuildEditorAsync(string sessionId, bool darkMode,
        CancellationToken cancellationToken = default) =>
        BuildAsync(true, sessionId, RemoteContentPolicy.Blocked, darkMode, cancellationToken);

    private static async Task<string> BuildAsync(bool editor, string sessionId, RemoteContentPolicy policy,
        bool darkMode, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(sessionId, "N", out _)) throw new ArgumentException("Expected a generation GUID.", nameof(sessionId));
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        cancellationToken.ThrowIfCancellationRequested();
        string nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        string remote = policy == RemoteContentPolicy.ImagesAndFontsAllowed ? " http: https:" : string.Empty;
        string csp = $"default-src 'none'; script-src 'nonce-{nonce}'; style-src 'unsafe-inline'; " +
            $"img-src data:{remote}; font-src data:{remote}; connect-src 'none'; media-src 'none'; " +
            "frame-src 'none'; child-src 'none'; object-src 'none'; worker-src 'none'; base-uri 'none'; form-action 'none'";
        string[] scripts = editor
            ? ["dompurify-3.4.14.min.js", "mail-colors.js", "linkify.min.js", "linkify-element.min.js", "editor.js", "editor-images.js", "editor-tables.js"]
            : ["mail-colors.js", "linkify.min.js", "linkify-element.min.js", "dompurify-3.4.14.min.js", "readability-0.6.0.js", "reader.js"];
        string document = await EditorDocumentAssets.BuildDocumentAsync(editor ? "editor.html" : "reader.html",
            editor ? "editor.css" : "reader.css", csp, scripts).ConfigureAwait(false);
        document = document.Replace($"nonce=\"{EditorDocumentAssets.ProcessScriptNonce}\"", $"nonce=\"{nonce}\"", StringComparison.Ordinal);
        string bootstrap = $"<script nonce=\"{nonce}\">window.winoSessionId={JsonSerializer.Serialize(sessionId, EditorJsonContext.Default.String)};window.winoUseCommandKey=true;</script>";
        // Policy and trusted generation bootstrap precede every stylesheet/script and all mail parsing.
        int metaEnd = document.IndexOf(">", document.IndexOf("<meta http-equiv=\"Content-Security-Policy\"", StringComparison.Ordinal), StringComparison.Ordinal);
        document = document.Insert(metaEnd + 1, bootstrap);
        if (darkMode) document = document.Replace("data-theme=\"light\"", "data-theme=\"dark\"", StringComparison.Ordinal);
        cancellationToken.ThrowIfCancellationRequested();
        return document;
    }
}

/// <summary>Typed script result envelope retained for bridge serialization compatibility.</summary>
public sealed record MacOsScriptResult
{
    [JsonPropertyName("text")] public string? Text { get; init; }
}
