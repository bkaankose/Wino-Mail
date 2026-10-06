using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.Generic;
using Wino.Editor;
using Wino.Mail.Controls.Playground.Lifetime;

namespace Wino.Mail.Controls.Playground.Pages;

public sealed partial class EditorPage : Page, IDisposable, IPlaygroundLifetimeAware
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

    // An Outlook-authored layout: head styles, a body background, a fixed-width card,
    // a spacer image, and a fixed-position element that must not overlay the reader.
    private const string OutlookHtml = """
        <html><head><style>
        body { background: #f3f3f3; margin: 0; }
        p.MsoNormal { margin: 0; font-family: Calibri, sans-serif; font-size: 11pt; color: #1f3864; }
        .card { width: 600px; background: #ffffff; border: 1px solid #dddddd; }
        .banner { position: fixed; top: 0; left: 0; background: #ff0000; color: #ffffff; }
        </style></head>
        <body>
        <table class="card" cellpadding="16" cellspacing="0"><tr><td>
        <p class="MsoNormal">Hi Avery,</p>
        <img src="data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7" width="1" height="24" alt="">
        <p class="MsoNormal">The quarterly review is attached. Paragraphs use Outlook's zero margins, and the spacer image above keeps its 24 pixel height.</p>
        <p class="MsoNormal"><a href="#details">Jump to details</a></p>
        <div class="banner">This fixed banner must scroll with the message.</div>
        <h3 id="details" style="color:#000000">Details</h3>
        <p class="MsoNormal">Regards,<br>Morgan</p>
        </td></tr></table>
        </body></html>
        """;

    // A promotional layout whose saturated colors must survive the dark reading surface.
    private const string BrandHtml = """
        <table width="600" cellpadding="0" cellspacing="0" style="background:#ffffff;border:1px solid #e0e0e0">
          <tr><td style="background:#0b3d91;color:#ffffff;padding:16px;font:bold 20px Segoe UI">Contoso Airlines</td></tr>
          <tr><td style="background-image:linear-gradient(135deg,#ffd54f,#ff8a65);color:#3e2723;padding:24px">Gradient hero text keeps the sender's colors.</td></tr>
          <tr><td style="padding:16px;color:#333333">Your trip to Lisbon is confirmed. Light panels become dark surfaces, while the brand bar, the gradient, and the button stay as designed.</td></tr>
          <tr><td style="padding:0 16px 16px"><div style="background:#fff3cd;color:#856404;border:1px solid #ffeeba;padding:8px">Check-in opens 24 hours before departure.</div></td></tr>
          <tr><td style="padding:0 16px 16px"><a href="https://example.com/trip" style="background:#c62828;color:#ffffff;padding:10px 18px;text-decoration:none;display:inline-block">Manage booking</a></td></tr>
        </table>
        """;

    // A sender that ships its own dark palette. The reader must use it instead of adapting colors.
    private const string SenderDarkHtml = """
        <html><head>
        <meta name="color-scheme" content="light dark">
        <style>
        body { background: #ffffff; color: #111111; }
        .panel { background: #e8f0fe; color: #0b3d91; padding: 12px; }
        @media (prefers-color-scheme: dark) {
          body { background: #0d1b2a !important; color: #e0e6ef !important; }
          .panel { background: #1b263b !important; color: #9ec5fe !important; }
        }
        </style></head>
        <body><p>This newsletter declares its own dark mode.</p><div class="panel">Its dark palette is used as the sender designed it.</div></body></html>
        """;

    private bool _rendererLoaded;
    private HtmlMailRenderMode _renderMode;
    private string _scenario = "Article";
    private Task _composeLoadedTask = Task.CompletedTask;
    private Task _rendererLoadedTask = Task.CompletedTask;
    private bool _disposed;

    IEnumerable<object> IPlaygroundLifetimeAware.AdditionalLifetimeObjects =>
        (object[])[ComposeEditor, MailRenderer];

    public EditorPage()
    {
        InitializeComponent();
        ComposeEditor.ApplicationShortcutRequested += ComposeEditor_ApplicationShortcutRequested;
    }

    private void ComposeEditor_Loaded(object sender, RoutedEventArgs e)
    {
        _composeLoadedTask = InitializeComposeEditorAsync();
    }

    private async Task InitializeComposeEditorAsync()
    {
        await ComposeEditor.ConfigureSpellCheckAsync(true, "en-US");
        await ComposeEditor.ConfigureAutoCorrectAsync(true);
        await ComposeEditor.SetHtmlAsync("<p>Hi team,</p><p>Here is the latest design review summary. Please add comments before Friday.</p><p>Thanks,<br/>Avery</p>");
        await ComposeEditor.SetApplicationShortcutsAsync(
            new List<EditorApplicationShortcutGesture>
            {
                new("Enter", true, false, false)
            });
    }

    private void MailRenderer_Loaded(object sender, RoutedEventArgs e)
    {
        _rendererLoadedTask = InitializeMailRendererAsync();
    }

    private async Task InitializeMailRendererAsync()
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
            "Outlook" => OutlookHtml,
            "Brand" => BrandHtml,
            "SenderDark" => SenderDarkHtml,
            _ => ArticleHtml,
        };
        await MailRenderer.RenderHtmlAsync(html, _renderMode);
        RendererScenarioStatus.Text = $"{_renderMode} · {_scenario}";
    }

    private async void RendererDarkMode_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch toggle) await MailRenderer.SetThemeAsync(toggle.IsOn);
    }

    private async void RendererBlockRemote_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle) return;
        MailRenderer.BlockRemoteResources = toggle.IsOn;
        if (_rendererLoaded) await RenderSelectedScenarioAsync();
    }

    private void ComposeDarkMode_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch toggle) ComposeEditor.IsEditorDarkMode = toggle.IsOn;
    }

    private void ComposeEditor_ApplicationShortcutRequested(object? sender, EditorApplicationShortcutGesture e)
        => ApplicationShortcutStatus.Text = $"Application shortcut forwarded: Ctrl+{e.Key}";

    async Task IPlaygroundLifetimeAware.PrepareForLifetimeTestAsync(CancellationToken cancellationToken)
    {
        EditorPlaygroundTabView.SelectedIndex = 0;
        await WaitUntilLoadedAsync(ComposeEditor, cancellationToken);
        await _composeLoadedTask.WaitAsync(cancellationToken);

        EditorPlaygroundTabView.SelectedIndex = 1;
        await WaitUntilLoadedAsync(MailRenderer, cancellationToken);
        await _rendererLoadedTask.WaitAsync(cancellationToken);
    }

    private static async Task WaitUntilLoadedAsync(FrameworkElement element, CancellationToken cancellationToken)
    {
        if (element.IsLoaded)
        {
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void Loaded(object sender, RoutedEventArgs args) => completion.TrySetResult();

        element.Loaded += Loaded;
        try
        {
            if (!element.IsLoaded)
            {
                await completion.Task.WaitAsync(cancellationToken);
            }
        }
        finally
        {
            element.Loaded -= Loaded;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ComposeEditor.ApplicationShortcutRequested -= ComposeEditor_ApplicationShortcutRequested;
        ComposeEditor.Loaded -= ComposeEditor_Loaded;
        MailRenderer.Loaded -= MailRenderer_Loaded;
        ComposeEditor.Dispose();
        MailRenderer.Dispose();
        ComposeEditorHost.Child = null;
        MailRendererHost.Child = null;
        EditorPlaygroundTabView.TabItems.Clear();
        _composeLoadedTask = Task.CompletedTask;
        _rendererLoadedTask = Task.CompletedTask;
        Content = null;
        GC.SuppressFinalize(this);
    }
}
