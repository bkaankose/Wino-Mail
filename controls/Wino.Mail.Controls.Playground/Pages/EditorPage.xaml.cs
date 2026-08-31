using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.Generic;
using Wino.Editor;

namespace Wino.Mail.Controls.Playground.Pages;

public sealed partial class EditorPage : Page
{
    private const string ArticleHtml = """
        <nav>Issue archive · Account · Preferences · Unsubscribe</nav>
        <article lang="en" dir="ltr">
          <h1>A practical guide to calmer inboxes</h1>
          <p>A useful reading workflow starts by separating urgent work from messages that merely look urgent. The first pass should identify direct requests, time-sensitive decisions, and information that changes what you will do next. Everything else can wait for a deliberate review window.</p>
          <p>Reader-focused presentation helps because visual campaigns, repeated navigation, and oversized promotional elements no longer compete with the message itself. The meaningful paragraphs remain in order, links still work, and the original source stays available to the host for later operations.</p>
          <p>The best workflow is intentionally small: decide whether a response is required, capture any concrete task, and archive reference material when it no longer needs attention. Repeating those steps is more valuable than inventing a complicated folder system that needs constant maintenance.</p>
        </article>
        <footer>Privacy · Terms · View in browser</footer>
        """;

    private const string FallbackHtml = "<p>Short confirmation: the meeting starts at 10:00.</p>";

    private const string NewsletterHtml = """
        <header><a href="https://example.com/archive">Archive</a> · <a href="https://example.com/preferences">Preferences</a></header>
        <aside><h2>Today only</h2><p>Buy three unrelated products and invite ten friends.</p></aside>
        <main><article>
          <h1>Engineering weekly</h1>
          <p>This week the team completed the offline renderer migration and documented the boundaries between host code and reusable controls. The change keeps rendering deterministic and makes the security boundary easier to audit.</p>
          <p>The primary story explains why detached parsing matters. Content extraction runs away from the live document, so untrusted message markup cannot become active merely because the reader is deciding which paragraphs are relevant. Sanitization still happens before extraction and again before insertion.</p>
          <p>Next week the team will compare representative newsletters, receipts, personal mail, and long-form updates. These examples are deliberately local and stable, which makes regressions easier to understand than tests that depend on changing network content.</p>
        </article></main>
        <aside>Sponsored links · Social channels · Download our app</aside>
        <footer>Company address · Legal notice · Unsubscribe</footer>
        """;

    private const string ImageHtml = """
        <article><h1>Field report with a relevant image</h1>
        <img src="data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M/wHwAF/gL+ZQyKAAAAAElFTkSuQmCC" alt="Readability relevant image">
        <p>The image belongs to the report rather than a tracking or navigation surface. This paragraph and the following explanation provide enough meaningful context for article extraction while the embedded data image remains fully offline.</p>
        <p>Relevant media should remain near the text that explains it. The renderer constrains oversized images to the reading surface without replacing the source or inventing a network dependency.</p>
        <p>Additional deterministic prose ensures that stock Readability thresholds treat this as a substantive message instead of a short notification. The output should contain the article text and the image with its accessible alternative text.</p></article>
        """;

    private const string HostileHtml = """
        <article onclick="window.hostileClick=true"><h1>Hostile message</h1>
        <script>window.hostileScript=true</script>
        <p><a href="javascript:window.hostileLink=true">Unsafe link</a> Safe explanatory text remains visible after sanitization.</p>
        <img src="x" onerror="window.hostileImage=true" alt="Broken hostile image">
        <iframe srcdoc="<script>window.hostileFrame=true</script>"></iframe>
        <form><input name="secret" value="must-not-survive"><button>Submit</button><p>Form-only content</p></form>
        <object data="https://example.com/active"></object><embed src="https://example.com/active">
        <p>The remaining paragraphs are intentionally long enough to exercise the same detached extraction path as ordinary content. No script, event attribute, unsafe protocol, form, frame, or embedded document may survive either sanitization pass.</p>
        <p>A second safe paragraph makes the fallback observable even if stock Readability decides that this synthetic article is not suitable for extraction.</p></article>
        """;

    private bool _rendererLoaded;
    private HtmlMailRenderMode _renderMode;
    private string _scenario = "Article";

    public EditorPage()
    {
        InitializeComponent();
        ComposeEditor.ApplicationShortcutRequested += ComposeEditor_ApplicationShortcutRequested;
    }

    private async void ComposeEditor_Loaded(object sender, RoutedEventArgs e)
    {
        await ComposeEditor.SetHtmlAsync("<p>Hi team,</p><p>Here is the latest design review summary. Please add comments before Friday.</p><p>Thanks,<br/>Avery</p>");
        await ComposeEditor.SetApplicationShortcutsAsync(
            new List<EditorApplicationShortcutGesture>
            {
                new("Enter", true, false, false)
            });
    }

    private async void MailRenderer_Loaded(object sender, RoutedEventArgs e)
    {
        _rendererLoaded = true;
        await RenderSelectedScenarioAsync();
    }

    private async void RenderMode_Checked(object sender, RoutedEventArgs e)
    {
        _renderMode = string.Equals((sender as FrameworkElement)?.Tag as string, "Readability", System.StringComparison.Ordinal)
            ? HtmlMailRenderMode.Readability
            : HtmlMailRenderMode.Original;
        if (_rendererLoaded) await RenderSelectedScenarioAsync();
    }

    private async void RenderScenario_Click(object sender, RoutedEventArgs e)
    {
        _scenario = (sender as FrameworkElement)?.Tag as string ?? "Article";
        if (_rendererLoaded) await RenderSelectedScenarioAsync();
    }

    private async System.Threading.Tasks.Task RenderSelectedScenarioAsync()
    {
        var html = _scenario switch
        {
            "Fallback" => FallbackHtml,
            "Newsletter" => NewsletterHtml,
            "Image" => ImageHtml,
            "Hostile" => HostileHtml,
            _ => ArticleHtml,
        };
        await MailRenderer.RenderHtmlAsync(html, _renderMode);
        RendererScenarioStatus.Text = $"{_renderMode} · {_scenario}";
    }

    private void ComposeEditor_ApplicationShortcutRequested(object? sender, EditorApplicationShortcutGesture e)
        => ApplicationShortcutStatus.Text = $"Application shortcut forwarded: Ctrl+{e.Key}";
}
