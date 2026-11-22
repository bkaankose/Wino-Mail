using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Core.AccountIcon;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Controls.Linux.Shell;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.Shell;

internal enum WinoShellRowTitleWeight
{
    Regular,
    Medium,
    Semibold
}

/// <summary>What the shared <see cref="ShellMenuRowContent"/> does not carry. All optional.</summary>
internal sealed record WinoShellRowExtras(
    string? TitleColorHex = null,
    string? TitleChipHex = null,
    string? GlyphTintHex = null,
    IAccountIconInfo[]? AccountStack = null,
    bool CompactAccount = false,
    bool IsSelected = false,
    bool AccentTitle = false,
    WinoShellRowTitleWeight Weight = WinoShellRowTitleWeight.Regular,
    bool ShowsAction = false,
    bool IsAttentionRow = false,
    bool UsesAccountIcon = false,
    string? Tooltip = null,
    string? AccessibleName = null);

/// <summary>
/// One sidebar row (the Windows ShellMenuTemplates row and the macOS ShellPaneCell). The widget tree is built once and
/// switched by <see cref="Apply"/>, so a content change never rebuilds the row, moves focus or flickers.
/// </summary>
internal sealed class WinoShellRow : IDisposable
{
    private const int ChildIndent = 12;
    private const string GlyphPage = "glyph";
    private const string AccountPage = "account";
    private const string StackPage = "stack";

    private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
    private readonly Gtk.Box _pipe = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
    private readonly Gtk.Stack _leading = Gtk.Stack.New();
    private readonly WinoIcon _glyph = new(WinoIconGlyph.Folder, 16, WinoIconMode.FollowStyle);
    private readonly WinoAccountIcon _accountIcon = new(22);
    private readonly WinoAccountIconStack _accountStack = new(22);
    private readonly Gtk.Box _text = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
    private readonly Gtk.Label _title = Gtk.Label.New(string.Empty);
    private readonly Gtk.Label _subtitle = Gtk.Label.New(string.Empty);
    private readonly Gtk.ProgressBar _sync = Gtk.ProgressBar.New();
    private readonly Gtk.Button _attention;
    private readonly WinoUnreadPill _pill = new();
    private readonly Gtk.Button _action;
    private readonly Gtk.Button _chevron = Gtk.Button.New();
    private readonly Gtk.Label _chevronGlyph = WinoIcons.Label(WinoIconGlyph.ChevronRight, 10);
    private readonly IDisposable[] _signals;
    private string _kindClass = string.Empty;
    private uint _pulse;
    private bool _disposed;

    public WinoShellRow(string attentionLabel, string actionLabel)
    {
        _root.AddCssClass(WinoCssClass.ShellRow);
        _root.SetMarginEnd(4);

        _pipe.AddCssClass(WinoCssClass.SelectionPipe);
        _pipe.SetValign(Gtk.Align.Center);
        _pipe.SetOpacity(0);
        _root.Append(_pipe);

        _leading.SetTransitionType(Gtk.StackTransitionType.None);
        _leading.SetValign(Gtk.Align.Center);
        _leading.AddNamed(_glyph.Root, GlyphPage);
        _leading.AddNamed(_accountIcon.Root, AccountPage);
        _leading.AddNamed(_accountStack.Root, StackPage);
        _root.Append(_leading);

        _title.SetXalign(0);
        _title.SetEllipsize(Pango.EllipsizeMode.End);
        _title.SetSingleLineMode(true);
        _subtitle.SetXalign(0);
        _subtitle.SetEllipsize(Pango.EllipsizeMode.End);
        _subtitle.AddCssClass("caption");
        _subtitle.AddCssClass("dim-label");
        _sync.AddCssClass("osd");
        _sync.AddCssClass(WinoCssClass.ShellSync);
        _sync.SetVisible(false);
        _text.SetHexpand(true);
        _text.SetValign(Gtk.Align.Center);
        _text.Append(_title);
        _text.Append(_subtitle);
        _text.Append(_sync);
        _root.Append(_text);

        _attention = WinoIcons.Button(WinoIconGlyph.Warning, attentionLabel);
        _attention.AddCssClass("warning");
        _attention.SetVisible(false);
        _root.Append(_attention);
        _root.Append(_pill.Root);
        _action = WinoIcons.Button(WinoIconGlyph.Folder, actionLabel);
        _action.SetVisible(false);
        _root.Append(_action);

        // The row is the tab stop; Left and Right expand and collapse from the keyboard, so the chevron is pointer-only
        // and hidden from assistive technology (the list item reports expanded or collapsed through its name).
        _chevron.AccessibleRole = Gtk.AccessibleRole.Presentation;
        _chevron.SetFocusable(false);
        _chevron.SetFocusOnClick(false);
        _chevron.AddCssClass("flat");
        _chevron.AddCssClass("circular");
        _chevron.SetValign(Gtk.Align.Center);
        _chevron.SetChild(_chevronGlyph);
        _chevron.SetVisible(false);
        _root.Append(_chevron);

        _signals =
        [
            GtkSignals.OnClicked(_chevron, () => ChevronClicked?.Invoke()),
            GtkSignals.OnClicked(_attention, () => AttentionClicked?.Invoke()),
            GtkSignals.OnClicked(_action, () => ActionClicked?.Invoke()),
        ];
    }

    public Gtk.Widget Root => _root;

    public Gtk.Widget ChevronArea => _chevron;

    /// <summary>The title, the expansion state and the unread count, for the list item's accessible name.</summary>
    public string AccessibleName { get; private set; } = string.Empty;

    public string Title { get; private set; } = string.Empty;

    public event Action? ChevronClicked;
    public event Action? AttentionClicked;
    public event Action? ActionClicked;

    public void Apply(ShellMenuRowContent content, WinoShellRowExtras extras)
    {
        var header = content.Kind == ShellMenuRowKind.Header;
        SetKind(header ? WinoCssClass.ShellRowHeader
            : extras.IsAttentionRow ? WinoCssClass.ShellRowAttention
            : content.Kind switch
            {
                ShellMenuRowKind.NewItem => WinoCssClass.ShellRowNew,
                ShellMenuRowKind.Account => WinoCssClass.ShellRowAccount,
                _ => WinoCssClass.ShellRowFolder
            });
        _root.SetMarginStart(4 + content.Indent * ChildIndent);
        _root.SetOpacity(content.IsEnabled ? 1 : 0.45);
        Toggle(_root, WinoCssClass.ShellRowCompact, extras.CompactAccount && content.Kind == ShellMenuRowKind.Account);
        Toggle(_root, "wino-disabled", !content.IsEnabled);
        _root.SetTooltipText(extras.Tooltip);
        Title = content.Title;

        _pipe.SetVisible(!header);
        _leading.SetVisible(!header);
        _title.SetWrap(extras.IsAttentionRow);
        _title.SetLines(extras.IsAttentionRow ? 2 : 1);
        _title.SetSingleLineMode(!extras.IsAttentionRow);
        _title.SetEllipsize(extras.IsAttentionRow ? Pango.EllipsizeMode.None : Pango.EllipsizeMode.End);
        Toggle(_title, "heading", header || content.Kind == ShellMenuRowKind.NewItem);
        Toggle(_title, "dim-label", header);
        Toggle(_title, "warning", extras.IsAttentionRow);

        if (header)
        {
            SetTitle(content.Title, null, null);
            _subtitle.SetVisible(false);
            SetSync(false, false, 0);
            _attention.SetVisible(false);
            _pill.Count = 0;
            _action.SetVisible(false);
            _chevron.SetVisible(false);
            UpdateAccessibleName(content);
            return;
        }

        ApplyLeading(content, extras);
        SetTitle(content.Title, extras.TitleColorHex, extras.TitleChipHex);
        var subtitle = content.Kind == ShellMenuRowKind.Account && !extras.CompactAccount ? content.Subtitle : null;
        _subtitle.SetText(subtitle ?? string.Empty);
        _subtitle.SetVisible(!string.IsNullOrWhiteSpace(subtitle));
        SetSync(content.ShowsSync, content.IsSyncIndeterminate, content.SyncValue);
        _attention.SetVisible(content.IsAttentionRequired && content.Kind == ShellMenuRowKind.Account);
        _pill.Count = content.UnreadCount;
        _action.SetVisible(extras.ShowsAction);
        _chevron.SetVisible(content.IsExpanded is not null);
        if (content.IsExpanded is { } expanded) WinoIcons.SetGlyph(_chevronGlyph, WinoIcons.Glyph(expanded ? WinoIconGlyph.ChevronDown : WinoIconGlyph.ChevronRight));
        SetSelected(extras.IsSelected || content.ShowsIndicator, extras.AccentTitle, extras.Weight);
        AccessibleOverride = extras.AccessibleName;
        UpdateAccessibleName(content);
    }

    /// <summary>The cheap path for a selection move: the pipe, the accent and the title weight.</summary>
    public void SetSelected(bool selected, bool accentTitle, WinoShellRowTitleWeight weight)
    {
        _pipe.SetOpacity(selected ? 1 : 0);
        Toggle(_root, WinoCssClass.Selected, selected);
        Toggle(_title, "accent", accentTitle);
        Toggle(_title, WinoCssClass.WeightMedium, weight == WinoShellRowTitleWeight.Medium);
        Toggle(_title, WinoCssClass.WeightSemibold, weight == WinoShellRowTitleWeight.Semibold);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopPulse();
        foreach (var signal in _signals) signal.Dispose();
        ChevronClicked = null;
        AttentionClicked = null;
        ActionClicked = null;
    }

    private string? AccessibleOverride { get; set; }

    private void UpdateAccessibleName(ShellMenuRowContent content)
    {
        var name = string.IsNullOrWhiteSpace(AccessibleOverride) ? content.Title : AccessibleOverride;
        if (_pill.Count > 0) name += ", " + _pill.AccessibleText;
        AccessibleName = name;
    }

    private void ApplyLeading(ShellMenuRowContent content, WinoShellRowExtras extras)
    {
        var accountColumn = content.Kind is ShellMenuRowKind.Account or ShellMenuRowKind.NewItem;
        var size = accountColumn ? 22 : 20;
        _leading.SetSizeRequest(size, size);
        if (extras.AccountStack is { Length: > 1 } stack)
        {
            _accountStack.SetAccounts(stack);
            _leading.SetVisibleChildName(StackPage);
            return;
        }
        if (extras.UsesAccountIcon && content.AccountIcon is { } icon)
        {
            _accountIcon.Account = icon;
            _leading.SetVisibleChildName(AccountPage);
            return;
        }
        _glyph.SetIcon(content.Glyph == WinoIconGlyph.None ? WinoIconGlyph.Folder : content.Glyph);
        _glyph.Size = content.Kind == ShellMenuRowKind.Account ? 20 : 16;
        _glyph.TintHex = IsHexColor(extras.GlyphTintHex) ? extras.GlyphTintHex : null;
        _leading.SetVisibleChildName(GlyphPage);
    }

    private void SetTitle(string text, string? foreground, string? background)
    {
        var fg = IsHexColor(foreground) ? foreground : null;
        var bg = IsHexColor(background) ? background : null;
        Toggle(_title, WinoCssClass.ShellTitleChip, bg is not null);
        if (fg is null && bg is null)
        {
            _title.SetText(text);
            return;
        }
        var attributes = (fg is null ? string.Empty : $" foreground=\"{fg}\"") + (bg is null ? string.Empty : $" background=\"{bg}\"");
        _title.SetMarkup($"<span{attributes}>{GLib.Functions.MarkupEscapeText(text, -1)}</span>");
    }

    private void SetSync(bool visible, bool indeterminate, double value)
    {
        _sync.SetVisible(visible);
        if (!visible || !indeterminate) StopPulse();
        if (!visible) return;
        if (indeterminate)
        {
            if (_pulse == 0)
            {
                _sync.Pulse();
                _pulse = GLib.Functions.TimeoutAdd(GLib.Constants.PRIORITY_DEFAULT, 120, () =>
                {
                    if (_disposed)
                    {
                        _pulse = 0;
                        return false;
                    }
                    _sync.Pulse();
                    return true;
                });
            }
            return;
        }
        _sync.SetFraction(Math.Clamp(value / 100.0, 0, 1));
    }

    private void StopPulse()
    {
        if (_pulse == 0) return;
        GLib.Functions.SourceRemove(_pulse);
        _pulse = 0;
    }

    private void SetKind(string kindClass)
    {
        if (kindClass == _kindClass) return;
        if (_kindClass.Length > 0) _root.RemoveCssClass(_kindClass);
        _kindClass = kindClass;
        _root.AddCssClass(kindClass);
    }

    private static void Toggle(Gtk.Widget widget, string cssClass, bool on)
    {
        if (on) widget.AddCssClass(cssClass);
        else widget.RemoveCssClass(cssClass);
    }

    private static bool IsHexColor(string? value)
    {
        if (value is not { Length: 7 } || value[0] != '#') return false;
        for (var index = 1; index < value.Length; index++)
        {
            if (!Uri.IsHexDigit(value[index])) return false;
        }
        return true;
    }
}
