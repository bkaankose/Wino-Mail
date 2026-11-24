using Android.Content;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using Google.Android.Material.Button;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Editor;
using Wino.Mail.Controls.Android.Settings;
using Wino.Mail.Rendering;
using Wino.Presentation.Android;
using Wino.Renderer.Android;

namespace Wino.Mail.Android.Views.Dialogs;

/// <summary>
/// The rich text editor of signatures and email templates (Windows EngineMailEditorView): a formatting row over Wino's
/// own composer (<see cref="EngineMailEditorSession"/> on a MailView), set up with the composer's font, size, spell
/// check and autocorrect, in the app's light or dark theme. The session ends when the view leaves its window.
/// </summary>
public sealed class WinoHtmlTextEditor : LinearLayout
{
    private readonly MailView _view;
    private readonly IDispatcher _dispatcher;
    private EngineMailEditorSession? _session;
    private Task? _ready;

    /// <param name="minimumHeight">The minimum height dimension token of the editor (0: wino_editor_min_height).</param>
    public WinoHtmlTextEditor(Context context, string accessibilityLabel, int minimumHeight = 0) : base(context)
    {
        Orientation = Orientation.Vertical;
        var services = MainApplication.Services ?? throw new InvalidOperationException("The app's services are not ready.");
        _dispatcher = services.GetRequiredService<IDispatcher>();

        var tools = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        tools.AddView(Tool(context, WinoIconGlyph.TextBold, Translator.Composer_Bold, EditorCommand.ToggleBold()));
        tools.AddView(Tool(context, WinoIconGlyph.TextItalic, Translator.Composer_Italic, EditorCommand.ToggleItalic()));
        tools.AddView(Tool(context, WinoIconGlyph.TextUnderline, Translator.Composer_Underline, EditorCommand.ToggleUnderline()));
        tools.AddView(Tool(context, WinoIconGlyph.TextBulletList, Translator.Composer_BulletList, EditorCommand.ToggleUnorderedList()));
        tools.AddView(Tool(context, WinoIconGlyph.TextNumberList, Translator.Composer_OrderedList, EditorCommand.ToggleOrderedList()));
        tools.AddView(Tool(context, WinoIconGlyph.TextClearFormatting, Translator.Composer_ClearFormatting, EditorCommand.ClearFormatting()));
        var toolScroll = new HorizontalScrollView(context) { HorizontalScrollBarEnabled = false };
        toolScroll.AddView(tools);
        AddView(toolScroll);

        _view = new MailView(context) { ContentDescription = accessibilityLabel };
        _view.SetBackgroundResource(Resource.Drawable.wino_editor_background);
        int height = WinoTokens.Px(context, minimumHeight == 0 ? Resource.Dimension.wino_editor_min_height : minimumHeight);
        AddView(_view, new LayoutParams(ViewGroup.LayoutParams.MatchParent, height) { TopMargin = WinoTokens.Px(context, Resource.Dimension.wino_space_2xs) });

        var session = _session = new EngineMailEditorSession(_view, _dispatcher, services.GetRequiredService<MailEngineHost>(),
            services.GetRequiredService<IMailDialogService>());
        session.ContentChanged += (_, _) => _dispatcher.ExecuteOnUIThread(() => ContentChanged?.Invoke(this, EventArgs.Empty));
        _ready = PrepareAsync(session, context, services);
    }

    /// <summary>Raised on every edit.</summary>
    public event EventHandler? ContentChanged;

    /// <summary>Shows the HTML once the editor has started.</summary>
    public async Task SetHtmlAsync(string? html)
    {
        if (_session is not { } session || _ready is null) return;
        await _ready;
        await session.RenderHtmlAsync(html ?? string.Empty);
    }

    /// <summary>The content as HTML; null when the editor has already ended.</summary>
    public async Task<string?> GetHtmlAsync()
    {
        if (_session is not { } session) return null;
        try { return await session.GetHtmlBodyAsync() ?? string.Empty; }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException or OperationCanceledException) { return null; }
    }

    protected override void OnDetachedFromWindow()
    {
        base.OnDetachedFromWindow();
        if (_session is { } session)
        {
            _session = null;
            _ = DisposeSessionAsync(session);
        }
    }

    private static async Task DisposeSessionAsync(EngineMailEditorSession session)
    {
        try { await session.DisposeAsync(); }
        catch (Exception exception) { Serilog.Log.Warning(exception, "The signature or template editor did not end cleanly."); }
    }

    private static async Task PrepareAsync(EngineMailEditorSession session, Context context, IServiceProvider services)
    {
        await session.SetThemeAsync(WinoColors.IsDark(context));
        await session.InitializeAsync();
        if (services.GetService<IPreferencesService>() is { } preferences)
        {
            await session.SetDefaultTypographyAsync(preferences.ComposerFont, preferences.ComposerFontSize);
            await session.ExecuteCommandAsync(EditorCommand.ToggleSpellCheck(preferences.IsComposerSpellCheckEnabled));
            if (!string.IsNullOrWhiteSpace(preferences.ComposerSpellCheckLanguageCode))
                await session.ExecuteCommandAsync(EditorCommand.SetSpellCheckLanguage(preferences.ComposerSpellCheckLanguageCode));
            await session.ExecuteCommandAsync(EditorCommand.ToggleAutoCorrect(preferences.IsComposerAutoCorrectEnabled));
        }
    }

    private MaterialButton Tool(Context context, WinoIconGlyph glyph, string label, EditorCommand command)
    {
        var button = new MaterialButton(context, null, Resource.Attribute.materialIconButtonStyle)
        {
            Icon = WinoIcons.Drawable(context, glyph, WinoTokens.Dp(context, Resource.Dimension.wino_glyph_ml), WinoColors.PrimaryText(context)),
            ContentDescription = label,
            Focusable = false
        };
        button.IconTint = global::Android.Content.Res.ColorStateList.ValueOf(WinoColors.PrimaryText(context));
        if (OperatingSystem.IsAndroidVersionAtLeast(26)) button.TooltipText = label;
        button.Click += (_, _) => _ = RunAsync(command);
        return button;
    }

    // The keyboard's input connection restarts around a command, which changes the document under its composing text
    // and caret (as in the compose page's format bar).
    private async Task RunAsync(EditorCommand command)
    {
        if (_session is not { } session) return;
        try
        {
            RestartInput();
            await session.ExecuteCommandAsync(command);
            RestartInput();
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException) { }
    }

    private void RestartInput()
    {
        if (_view.HasFocus && Context?.GetSystemService(Context.InputMethodService) is InputMethodManager input)
            input.RestartInput(_view);
    }
}
