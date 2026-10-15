using TooltipCompat = AndroidX.AppCompat.Widget.TooltipCompat;
using Android.Content;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using Google.Android.Material.Dialog;
using Google.Android.Material.TextField;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Editor;
using Wino.Editor.Android;
using Wino.Mail.Android.Infrastructure;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.Mail.Compose;

/// <summary>
/// The format bar above the keyboard (canvas board Compose; the iOS keyboard accessory and the Windows Format tab, in the
/// same order): bold, italic, underline, strikethrough | bulleted and numbered lists, outdent, indent | alignment |
/// link, picture, clear formatting | undo, redo. It scrolls sideways on narrow screens. The buttons never take focus,
/// so the keyboard stays open; toggles follow the editor's selection state. The link dialog uses the shared
/// <see cref="EditorLinkRules"/>.
/// </summary>
public sealed partial class ComposeFragment
{
    private readonly List<(View Button, Func<EditorState, bool> IsActive)> _toggles = [];

    private void BuildFormatBar(View view, Context context)
    {
        _toggles.Clear();
        if (view.FindViewById<LinearLayout>(Resource.Id.compose_format_bar) is not { } bar) return;
        bar.RemoveAllViews();
        bar.ContentDescription = Translator.Composer_EditorToolbarLabel;

        Toggle(bar, context, WinoIconGlyph.TextBold, Translator.Composer_Bold, EditorCommand.ToggleBold(), s => s.IsBold);
        Toggle(bar, context, WinoIconGlyph.TextItalic, Translator.Composer_Italic, EditorCommand.ToggleItalic(), s => s.IsItalic);
        Toggle(bar, context, WinoIconGlyph.TextUnderline, Translator.Composer_Underline, EditorCommand.ToggleUnderline(), s => s.IsUnderline);
        Toggle(bar, context, WinoIconGlyph.TextStrikethrough, Translator.Composer_Strikethrough, EditorCommand.ToggleStrikethrough(), s => s.IsStrikethrough);
        Divider(bar, context);
        Toggle(bar, context, WinoIconGlyph.TextBulletList, Translator.Composer_BulletList, EditorCommand.ToggleUnorderedList(), s => s.IsUnorderedList);
        Toggle(bar, context, WinoIconGlyph.TextNumberList, Translator.Composer_OrderedList, EditorCommand.ToggleOrderedList(), s => s.IsOrderedList);
        Button(bar, context, WinoIconGlyph.TextIndentDecrease, Translator.Composer_Outdent, () => Run(EditorCommand.Outdent()));
        Button(bar, context, WinoIconGlyph.TextIndentIncrease, Translator.Composer_Indent, () => Run(EditorCommand.Indent()));
        Divider(bar, context);
        Toggle(bar, context, WinoIconGlyph.TextAlignLeft, Translator.Composer_AlignLeft, EditorCommand.SetAlignment(EditorTextAlignment.Left), s => s.Alignment == EditorTextAlignment.Left);
        Toggle(bar, context, WinoIconGlyph.TextAlignCenter, Translator.Composer_AlignCenter, EditorCommand.SetAlignment(EditorTextAlignment.Center), s => s.Alignment == EditorTextAlignment.Center);
        Toggle(bar, context, WinoIconGlyph.TextAlignRight, Translator.Composer_AlignRight, EditorCommand.SetAlignment(EditorTextAlignment.Right), s => s.Alignment == EditorTextAlignment.Right);
        Divider(bar, context);
        Button(bar, context, WinoIconGlyph.Link, Translator.Composer_InsertLink, ShowLinkDialog);
        Button(bar, context, WinoIconGlyph.ImageAdd, Translator.Composer_InsertPictureDescription, () => Run(EditorCommand.InsertImage()));
        Button(bar, context, WinoIconGlyph.TextClearFormatting, Translator.Composer_ClearFormatting, () => Run(EditorCommand.ClearFormatting()));
        Divider(bar, context);
        Button(bar, context, WinoIconGlyph.ArrowUndo, Translator.MacOSMenu_Undo, () => Run(EditorCommand.Undo()));
        Button(bar, context, WinoIconGlyph.ArrowRedo, Translator.MacOSMenu_Redo, () => Run(EditorCommand.Redo()));
        if (_editor?.CurrentState is { } state) UpdateFormatState(state);
    }

    private void Run(EditorCommand command)
    {
        if (_editor is not { } editor || _editorDisposed) return;
        Observe(RunAsync(editor, command));
    }

    /// <summary>
    /// Runs the command between two restarts of the keyboard's input connection: a command changes the document under
    /// the keyboard's composing text and caret, and a keyboard that kept its old offsets would type at the wrong place.
    /// </summary>
    private async Task RunAsync(AndroidHtmlMailEditorSession editor, EditorCommand command)
    {
        // The word being composed is committed first, so the command and the keyboard agree on the caret afterwards.
        await RestartKeyboardInputAsync();
        await editor.ExecuteCommandAsync(command);
        await RestartKeyboardInputAsync();
    }

    private Task RestartKeyboardInputAsync() => _dispatcher?.ExecuteOnUIThread(() =>
    {
        if (_editorDisposed || _web is not { } web || !web.HasFocus) return;
        if (web.Context?.GetSystemService(Context.InputMethodService) is global::Android.Views.InputMethods.InputMethodManager input)
            input.RestartInput(web);
    }) ?? Task.CompletedTask;

    private void Toggle(LinearLayout bar, Context context, WinoIconGlyph glyph, string label, EditorCommand command, Func<EditorState, bool> isActive)
    {
        var button = Button(bar, context, glyph, label, () => Run(command));
        _toggles.Add((button, isActive));
    }

    private View Button(LinearLayout bar, Context context, WinoIconGlyph glyph, string label, Action invoked)
    {
        // compose_format_button.xml: never focusable, so the editor keeps focus and the keyboard stays open.
        var button = LayoutInflater.From(context)?.Inflate(Resource.Layout.compose_format_button, bar, false) as TextView
            ?? throw new InvalidOperationException("The format button layout did not inflate.");
        button.ContentDescription = label;
        WinoIcons.Apply(button, glyph, ComposeStyle.Dp(context, Resource.Dimension.wino_compose_glyph_format));
        var background = new GradientDrawable();
        background.SetCornerRadius(ComposeStyle.Px(context, Resource.Dimension.wino_compose_format_button_radius));
        background.SetColor(global::Android.Graphics.Color.Transparent);
        button.Background = new RippleDrawable(global::Android.Content.Res.ColorStateList.ValueOf(WinoColors.SubtleFill(context)), background, null);
        TooltipCompat.SetTooltipText(button, label);
        button.Click += (_, _) => invoked();
        bar.AddView(button);
        return button;
    }

    private static void Divider(LinearLayout bar, Context context)
    {
        if (LayoutInflater.From(context)?.Inflate(Resource.Layout.compose_format_divider, bar, false) is { } divider) bar.AddView(divider);
    }

    /// <summary>Active toggles get the accent tint (Bold while the caret is in bold text, the current alignment).</summary>
    private void UpdateFormatState(EditorState state)
    {
        foreach (var (button, isActive) in _toggles)
        {
            if (button.Context is not { } context || button.Background is not RippleDrawable ripple || ripple.GetDrawable(0) is not GradientDrawable fill) continue;
            var active = isActive(state);
            fill.SetColor(active ? WinoColors.WithAlpha(WinoColors.Accent(context), ComposeStyle.Float(context, Resource.Dimension.wino_compose_format_active_alpha)) : global::Android.Graphics.Color.Transparent);
            if (button is TextView text) text.SetTextColor(active ? WinoColors.AccentText(context) : WinoColors.PrimaryText(context));
            button.Selected = active;
        }
    }

    /// <summary>Insert or edit a link, or a selected image's link (Windows ShowLinkDialogAsync; compose_link_dialog.xml).</summary>
    private void ShowLinkDialog()
    {
        if (_editor is not { } editor || _editorDisposed || Activity is not { } activity) return;
        var state = editor.CurrentState;
        var title = EditorLinkRules.Kind(state) switch
        {
            EditorLinkDialogKind.EditImageLink => Translator.Composer_EditImageLink,
            EditorLinkDialogKind.EditLink => Translator.ComposerParity_EditLink,
            _ => Translator.Composer_InsertLink
        };
        if (LayoutInflater.From(activity)?.Inflate(Resource.Layout.compose_link_dialog, null, false) is not { } body) return;
        body.Require<TextInputLayout>(Resource.Id.link_url_layout).Hint = Translator.Composer_LinkUrl;
        var url = body.Require<TextInputEditText>(Resource.Id.link_url);
        url.Text = EditorLinkRules.InitialUrl(state) ?? string.Empty;
        var textLayout = body.Require<TextInputLayout>(Resource.Id.link_text_layout);
        textLayout.Hint = Translator.Composer_LinkText;
        var text = body.Require<TextInputEditText>(Resource.Id.link_text);
        text.Text = state.SelectedText ?? string.Empty;
        var hasText = EditorLinkRules.HasTextField(state);
        textLayout.Visibility = hasText ? ViewStates.Visible : ViewStates.Gone;
        var builder = new MaterialAlertDialogBuilder(activity)
            .SetTitle(title)
            .SetView(body)
            .SetPositiveButton(Translator.Buttons_Apply, (_, _) =>
            {
                if (EditorLinkRules.CreateLink(state, url.Text, hasText ? text.Text : null) is { } link) Run(EditorCommand.InsertLink(link));
            })
            .SetNegativeButton(Translator.Buttons_Cancel, (_, _) => { });
        if (EditorLinkRules.Kind(state) != EditorLinkDialogKind.InsertLink)
            builder.SetNeutralButton(Translator.Composer_RemoveLink, (_, _) => Run(EditorCommand.RemoveLink()));
        builder.Show();
        url.RequestFocus();
    }
}
