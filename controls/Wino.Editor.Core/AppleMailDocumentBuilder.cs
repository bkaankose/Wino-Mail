using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wino.Editor;

/// <summary>
/// Fresh shells for WKWebView and the Android WebView (one generation per load, a per-document script nonce and the
/// remote-content CSP). No untrusted mail is included in the shell. <c>useCommandKey</c> maps editor shortcuts to
/// Command (Apple) instead of Control (Android).
/// </summary>
public static class AppleMailDocumentBuilder
{
    // The shell scripts and styles never change, so they are assembled once; each load only swaps in its own
    // policy and nonce. The placeholder cannot occur in the packaged assets.
    private const string PolicyPlaceholder = "wino-content-security-policy-placeholder";

    private static readonly Lazy<Task<string>> ReaderTemplate = new(() => EditorDocumentAssets.BuildDocumentAsync("reader.html", "reader.css",
        PolicyPlaceholder, "mail-colors.js", "linkify.min.js", "linkify-element.min.js", "dompurify-3.4.14.min.js", "readability-0.6.0.js", "reader.js"));

    private static readonly Lazy<Task<string>> EditorTemplate = new(() => EditorDocumentAssets.BuildDocumentAsync("editor.html", "editor.css",
        PolicyPlaceholder, "dompurify-3.4.14.min.js", "mail-colors.js", "linkify.min.js", "linkify-element.min.js", "editor.js", "editor-images.js", "editor-tables.js"));

    public static Task<string> BuildReaderAsync(string sessionId, RemoteContentPolicy policy,
        bool darkMode, CancellationToken cancellationToken = default, bool useCommandKey = true) =>
        BuildAsync(false, sessionId, policy, darkMode, useCommandKey, cancellationToken);

    public static Task<string> BuildEditorAsync(string sessionId, bool darkMode,
        CancellationToken cancellationToken = default, bool useCommandKey = true) =>
        BuildAsync(true, sessionId, RemoteContentPolicy.Blocked, darkMode, useCommandKey, cancellationToken);

    private static async Task<string> BuildAsync(bool editor, string sessionId, RemoteContentPolicy policy,
        bool darkMode, bool useCommandKey, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(sessionId, "N", out _)) throw new ArgumentException("Expected a generation GUID.", nameof(sessionId));
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        cancellationToken.ThrowIfCancellationRequested();
        string nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        string remote = policy == RemoteContentPolicy.ImagesAndFontsAllowed ? " http: https:" : string.Empty;
        string csp = $"default-src 'none'; script-src 'nonce-{nonce}'; style-src 'unsafe-inline'; " +
            $"img-src data:{remote}; font-src data:{remote}; connect-src 'none'; media-src 'none'; " +
            "frame-src 'none'; child-src 'none'; object-src 'none'; worker-src 'none'; base-uri 'none'; form-action 'none'";
        string template = await (editor ? EditorTemplate : ReaderTemplate).Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        string document = template
            .Replace(PolicyPlaceholder, csp, StringComparison.Ordinal)
            .Replace($"nonce=\"{EditorDocumentAssets.ProcessScriptNonce}\"", $"nonce=\"{nonce}\"", StringComparison.Ordinal);
        string bootstrap = $"<script nonce=\"{nonce}\">window.winoSessionId={JsonSerializer.Serialize(sessionId, EditorJsonContext.Default.String)};window.winoUseCommandKey={(useCommandKey ? "true" : "false")};</script>";
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
