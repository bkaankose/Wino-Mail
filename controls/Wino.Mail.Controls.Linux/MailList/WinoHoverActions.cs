using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Core.HoverActions;
using Wino.Mail.Controls.Core.MailRow;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Controls.Linux.MailList;

public readonly record struct WinoHoverAction(HoverActionKind Kind, WinoIconGlyph Glyph, string Label);

/// <summary>
/// Up to three action buttons over a mail row (Windows <c>HoverActionsControl</c>, macOS <c>WinoMailRowView</c> hover bar).
/// The bar stays in the tree; shown and hidden are CSS states (opacity and transform), so showing it costs no layout.
/// </summary>
public sealed class WinoHoverActions : IDisposable
{
    public const int SlotCount = 3;

    private static readonly string[] PositionClasses = [WinoCssClass.HoverFromRight, WinoCssClass.HoverFromTop, WinoCssClass.HoverFromBottom];
    private static readonly string[] SizeClasses = [WinoCssClass.HoverSmall, WinoCssClass.HoverMedium, WinoCssClass.HoverLarge];
    private static readonly string[] AnimationClasses = [WinoCssClass.HoverPopup, WinoCssClass.HoverSlide, WinoCssClass.HoverNone];

    private readonly Gtk.Box _root;
    private readonly Gtk.Button[] _buttons = new Gtk.Button[SlotCount];
    private readonly Gtk.Label[] _glyphs = new Gtk.Label[SlotCount];
    private readonly WinoHoverAction[] _actions = new WinoHoverAction[SlotCount];
    private readonly IDisposable[] _clicks = new IDisposable[SlotCount];
    private string _glyphSize;
    private bool _shown;
    private bool _disposed;

    public WinoHoverActions()
    {
        WinoLiveObjects.Track(this);
        _root = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
        _root.AddCssClass(WinoCssClass.HoverActions);
        _root.SetCanTarget(false);
        _glyphSize = WinoIcons.SizeClass(14);
        for (var slot = 0; slot < SlotCount; slot++)
        {
            // The real label is set with the action; the helper needs one now.
            var button = WinoIcons.Button(WinoIconGlyph.Archive, Translator.MailOperation_Archive, 14);
            button.SetFocusable(false);
            button.SetFocusOnClick(false);
            button.AddCssClass(WinoCssClass.HoverButton);
            button.SetVisible(false);
            // Drawn invisible (opacity) the buttons stay in the AT-SPI tree, and GTK 4.22 ignores the hidden state
            // there, so a hidden button is insensitive: a screen reader or an agent cannot delete a row nobody hovers.
            button.SetSensitive(false);
            _glyphs[slot] = (Gtk.Label)(button.GetChild() ?? throw new InvalidOperationException("The hover button has no glyph."));
            _buttons[slot] = button;
            var index = slot;
            _clicks[slot] = GtkSignals.OnClicked(button, () =>
            {
                if (_shown && _actions[index].Kind != HoverActionKind.None) Invoked?.Invoke(_actions[index].Kind);
            });
            _root.Append(button);
        }
        SetPosition(HoverActionPosition.RightCenter);
        SetButtonSize(MailHoverActionButtonSize.Small);
        SetAnimation(HoverActionAnimation.Popup);
    }

    public Gtk.Widget Root => _root;

    public event Action<HoverActionKind>? Invoked;

    public bool HasActions { get; private set; }

    public bool IsShown => _shown;

    /// <summary>Up to three actions, left to right; a shorter array hides the remaining slots. Unchanged slots are not touched.</summary>
    public void SetActions(WinoHoverAction[] actions)
    {
        var any = false;
        for (var slot = 0; slot < SlotCount; slot++)
        {
            var action = slot < actions.Length ? actions[slot] : default;
            var visible = action.Kind != HoverActionKind.None;
            any |= visible;
            if (_actions[slot] == action) continue;
            _actions[slot] = action;
            if (visible)
            {
                WinoIcons.SetGlyph(_glyphs[slot], WinoIcons.Glyph(action.Glyph));
                WinoA11y.LabelWithTooltip(_buttons[slot], action.Label);
            }
            _buttons[slot].SetVisible(visible);
        }
        HasActions = any;
        if (!any) SetShown(false, false);
    }

    public void SetPosition(HoverActionPosition position)
    {
        var centred = position is HoverActionPosition.TopCenter or HoverActionPosition.BottomCenter;
        _root.SetHalign(centred ? Gtk.Align.Center : Gtk.Align.End);
        _root.SetValign(position switch
        {
            HoverActionPosition.RightTop or HoverActionPosition.TopCenter => Gtk.Align.Start,
            HoverActionPosition.RightBottom or HoverActionPosition.BottomCenter => Gtk.Align.End,
            _ => Gtk.Align.Center,
        });
        _root.SetMarginEnd(centred ? 0 : (int)WinoMailRowMetrics.InnerTrailing);
        _root.SetMarginTop(4);
        _root.SetMarginBottom(4);
        Exclusive(PositionClasses, position switch
        {
            HoverActionPosition.TopCenter => WinoCssClass.HoverFromTop,
            HoverActionPosition.BottomCenter => WinoCssClass.HoverFromBottom,
            _ => WinoCssClass.HoverFromRight,
        });
        // An alignment change alone does not reallocate an overlay child whose size stayed the same.
        _root.GetParent()?.QueueAllocate();
    }

    public void SetButtonSize(MailHoverActionButtonSize size)
    {
        Exclusive(SizeClasses, size switch
        {
            MailHoverActionButtonSize.Medium => WinoCssClass.HoverMedium,
            MailHoverActionButtonSize.Large => WinoCssClass.HoverLarge,
            _ => WinoCssClass.HoverSmall,
        });
        var glyphSize = WinoIcons.SizeClass(size switch
        {
            MailHoverActionButtonSize.Medium => 16,
            MailHoverActionButtonSize.Large => 20,
            _ => 14,
        });
        if (glyphSize == _glyphSize) return;
        foreach (var glyph in _glyphs)
        {
            glyph.RemoveCssClass(_glyphSize);
            glyph.AddCssClass(glyphSize);
        }
        _glyphSize = glyphSize;
    }

    public void SetAnimation(HoverActionAnimation animation)
        => Exclusive(AnimationClasses, animation switch
        {
            HoverActionAnimation.Slide => WinoCssClass.HoverSlide,
            HoverActionAnimation.NoAnimation => WinoCssClass.HoverNone,
            _ => WinoCssClass.HoverPopup,
        });

    /// <summary><paramref name="animate"/> false snaps (unbind, recycling).</summary>
    public void SetShown(bool shown, bool animate = true)
    {
        if (_shown == shown || _disposed) return;
        _shown = shown;
        _root.SetCanTarget(shown);
        foreach (var button in _buttons) button.SetSensitive(shown);
        if (!animate) _root.AddCssClass(WinoCssClass.HoverInstant);
        if (shown) _root.AddCssClass(WinoCssClass.HoverShown);
        else _root.RemoveCssClass(WinoCssClass.HoverShown);
        if (!animate)
        {
            var root = _root;
            MainLoop.Post(() => root.RemoveCssClass(WinoCssClass.HoverInstant));
        }
    }

    /// <summary>The labels of the visible slots, for Debug checks.</summary>
    public string Describe()
        => string.Join(",", _actions.Where(static action => action.Kind != HoverActionKind.None).Select(static action => action.Label));

#if DEBUG
    public bool DebugShows(HoverActionKind kind) => _shown && Array.Exists(_actions, action => action.Kind == kind);

    /// <summary>The click path of the slot showing <paramref name="kind"/>.</summary>
    public void DebugInvoke(HoverActionKind kind)
    {
        var slot = Array.FindIndex(_actions, action => action.Kind == kind);
        if (slot >= 0) _buttons[slot].Activate();
    }
#endif

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var click in _clicks) click.Dispose();
        Invoked = null;
    }

    private void Exclusive(string[] classes, string active)
    {
        foreach (var name in classes)
        {
            if (name == active) _root.AddCssClass(name);
            else _root.RemoveCssClass(name);
        }
    }
}
