using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Mail.Compose;

/// <summary>
/// Find (and replace) bar at the top of the composer body (Mac ComposeFindBar). Row one: the search field, the match
/// counter, the options menu (Ignore Case, Whole Words, Replace…), previous / next and Done. Row two (replace mode):
/// the replacement field, Replace and Replace All. The bar raises events; the controller drives the editor.
/// </summary>
internal sealed class ComposeFindBar : UIView
{
    private readonly UITextField _search;
    private readonly UILabel _counter;
    private readonly UIButton _options;
    private readonly UIButton _previous;
    private readonly UIButton _next;
    private readonly UITextField _replace;
    private readonly UIStackView _replaceRow;
    private readonly UIButton _replaceButton;
    private readonly UIButton _replaceAllButton;
    private bool _ignoreCase = true;
    private bool _wholeWords;

    public event EventHandler? QueryChanged;
    public event EventHandler? NextRequested;
    public event EventHandler? PreviousRequested;
    public event EventHandler? ReplaceRequested;
    public event EventHandler? ReplaceAllRequested;
    public event EventHandler? CloseRequested;

    public ComposeFindBar()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        AccessibilityLabel = Translator.Composer_FindBarLabel;
        ShouldGroupAccessibilityChildren = true;

        _search = Field(Translator.MailOperation_Find, WinoIconGlyph.Find);
        _search.ReturnKeyType = UIReturnKeyType.Search;
        _search.EditingChanged += (_, _) => { if (_search.MarkedTextRange is null) QueryChanged?.Invoke(this, EventArgs.Empty); };
        _search.ShouldReturn = _ => { NextRequested?.Invoke(this, EventArgs.Empty); return false; };

        _counter = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);
        _counter.SetContentCompressionResistancePriority(750, UILayoutConstraintAxis.Horizontal);
        _counter.SetContentHuggingPriority(750, UILayoutConstraintAxis.Horizontal);

        _options = GlyphButton(WinoIconGlyph.More, Translator.MailOperation_Find, null);
        _options.ShowsMenuAsPrimaryAction = true;
        _options.Menu = UIMenu.Create(Translator.MailOperation_Find, null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline,
            [UIDeferredMenuElement.CreateUncached(completion => completion(BuildOptions()))]);
        _previous = GlyphButton(WinoIconGlyph.ChevronUp, Translator.Composer_FindPrevious, () => PreviousRequested?.Invoke(this, EventArgs.Empty));
        _next = GlyphButton(WinoIconGlyph.ChevronDown, Translator.Composer_FindNext, () => NextRequested?.Invoke(this, EventArgs.Empty));
        var done = TextButton(Translator.Buttons_Done, () => CloseRequested?.Invoke(this, EventArgs.Empty), true);

        _replace = Field(Translator.Composer_Replace, WinoIconGlyph.Edit);
        _replace.ReturnKeyType = UIReturnKeyType.Done;
        _replace.ShouldReturn = _ => { ReplaceRequested?.Invoke(this, EventArgs.Empty); return false; };
        _replaceButton = TextButton(Translator.Composer_Replace, () => ReplaceRequested?.Invoke(this, EventArgs.Empty), false);
        _replaceAllButton = TextButton(Translator.Composer_ReplaceAll, () => ReplaceAllRequested?.Invoke(this, EventArgs.Empty), false);

        var findRow = WinoLayout.HStack(4, _search, _counter, _options, _previous, _next, done);
        _replaceRow = WinoLayout.HStack(6, _replace, _replaceButton, _replaceAllButton);
        _replaceRow.Hidden = true;
        _search.SetContentHuggingPriority(1, UILayoutConstraintAxis.Horizontal);
        _replace.SetContentHuggingPriority(1, UILayoutConstraintAxis.Horizontal);
        var rows = WinoLayout.VStack(6, findRow, _replaceRow);
        rows.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Insets(rows, 6, 12, 6, 8);
        var separator = new WinoSeparator();
        var stack = WinoLayout.VStack(0, rows, separator);
        stack.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Fill(stack, this);
        SetResult(0, 0);
    }

    public string Query => _search.Text ?? string.Empty;
    public string Replacement => _replace.Text ?? string.Empty;
    public bool IgnoreCase => _ignoreCase;
    public bool WholeWords => _wholeWords;
    public bool IsReplaceVisible => !_replaceRow.Hidden;
    public string CounterText => _counter.Text ?? string.Empty;

    /// <summary>True while the search or replacement field is being edited.</summary>
    public bool IsEditing => _search.IsFirstResponder || _replace.IsFirstResponder;

    public void SetQuery(string? query) => _search.Text = query ?? string.Empty;

    public void SetReplaceVisible(bool visible) => _replaceRow.Hidden = !visible;

    public void FocusSearch()
    {
        _search.BecomeFirstResponder();
        _search.SelectAll(this);
    }

    /// <summary>Shows "n of m", "Not found" or nothing for an empty query, and enables the actions that apply.</summary>
    public void SetResult(int index, int count)
    {
        var hasQuery = Query.Length > 0;
        _counter.Text = !hasQuery ? string.Empty
            : count == 0 ? Translator.Composer_FindNoMatches
            : string.Format(Translator.Composer_FindMatchCount, index, count);
        _counter.TextColor = hasQuery && count == 0 ? WinoStyle.Critical : WinoStyle.SecondaryText;
        _previous.Enabled = count > 0;
        _next.Enabled = count > 0;
        _replaceButton.Enabled = count > 0;
        _replaceAllButton.Enabled = count > 0;
    }

    private UIMenuElement[] BuildOptions() =>
    [
        EditorFormatToolbar.Action(Translator.Composer_FindIgnoreCase, () => { _ignoreCase = !_ignoreCase; QueryChanged?.Invoke(this, EventArgs.Empty); }, _ignoreCase),
        EditorFormatToolbar.Action(Translator.Composer_FindWholeWords, () => { _wholeWords = !_wholeWords; QueryChanged?.Invoke(this, EventArgs.Empty); }, _wholeWords),
        EditorFormatToolbar.Inline(
        [
            EditorFormatToolbar.Action(Translator.Composer_FindShowReplace, () =>
            {
                SetReplaceVisible(!IsReplaceVisible);
                if (IsReplaceVisible) _replace.BecomeFirstResponder();
            }, IsReplaceVisible)
        ])
    ];

    private static UITextField Field(string placeholder, WinoIconGlyph glyph)
    {
        var field = new UITextField
        {
            Placeholder = placeholder,
            BorderStyle = UITextBorderStyle.RoundedRect,
            Font = WinoStyle.Subheadline,
            AutocorrectionType = UITextAutocorrectionType.No,
            AutocapitalizationType = UITextAutocapitalizationType.None,
            ClearButtonMode = UITextFieldViewMode.WhileEditing,
            AdjustsFontForContentSizeCategory = true,
            TranslatesAutoresizingMaskIntoConstraints = false,
            AccessibilityLabel = placeholder
        };
        var icon = new UIImageView(WinoIcons.Image(glyph, 14)) { TintColor = WinoStyle.SecondaryText, ContentMode = UIViewContentMode.Center };
        icon.Frame = new CoreGraphics.CGRect(0, 0, 28, 20);
        field.LeftView = icon;
        field.LeftViewMode = UITextFieldViewMode.Always;
        field.HeightAnchor.ConstraintGreaterThanOrEqualTo(34).Active = true;
        return field;
    }

    private static UIButton GlyphButton(WinoIconGlyph glyph, string label, Action? action)
    {
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Image = WinoIcons.Image(glyph, 16, null, label);
        configuration.BaseForegroundColor = WinoStyle.PrimaryText;
        var button = new UIButton { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false, AccessibilityLabel = label };
        WinoLayout.Size(button, 36, 36);
        if (action is not null) button.PrimaryActionTriggered += (_, _) => action();
        return button;
    }

    private static UIButton TextButton(string title, Action action, bool strong)
    {
        var configuration = strong ? UIButtonConfiguration.PlainButtonConfiguration : UIButtonConfiguration.GrayButtonConfiguration;
        configuration.Title = title;
        configuration.ButtonSize = UIButtonConfigurationSize.Small;
        var button = new UIButton { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false };
        button.SetContentCompressionResistancePriority(1000, UILayoutConstraintAxis.Horizontal);
        button.SetContentHuggingPriority(750, UILayoutConstraintAxis.Horizontal);
        button.PrimaryActionTriggered += (_, _) => action();
        return button;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            QueryChanged = null;
            NextRequested = null;
            PreviousRequested = null;
            ReplaceRequested = null;
            ReplaceAllRequested = null;
            CloseRequested = null;
        }
        base.Dispose(disposing);
    }
}
