using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.Common;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>
/// The window-level info bar of one window: an overlay over the window content, top end, one message at a time,
/// queued. Main thread only; callers dispatch.
/// </summary>
internal sealed class InfoBarHost : IDisposable
{
    public static readonly TimeSpan DismissInterval = TimeSpan.FromSeconds(5);

    private sealed record Message(string Title, string Text, InfoBarMessageType Type, string? ActionText, Action? Action);

    private readonly Gtk.Overlay _root = Gtk.Overlay.New();
    private readonly Gtk.Box _card = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
    private readonly Queue<Message> _pending = new();
    private Message? _visible;
    private bool _suspended;

    public InfoBarHost()
    {
        Bar = new WinoInfoBar { IsClosable = true, AutoDismissInterval = DismissInterval };
        Bar.Closed += (_, _) => Next();
        Bar.ActionInvoked += _ => RunAction();

        _card.AddCssClass(WinoCssClass.InfoBarFloating);
        _card.SetHalign(Gtk.Align.End);
        _card.SetValign(Gtk.Align.Start);
        _card.SetMarginTop(56);
        _card.SetMarginEnd(24);
        _card.SetMarginStart(24);
        _card.SetMarginBottom(12);
        _card.Append(Bar.Root);
        _card.SetVisible(false);
        _root.AddOverlay(_card);
    }

    public Gtk.Overlay Root => _root;

    public WinoInfoBar Bar { get; }

    public Gtk.Widget? Child
    {
        get => _root.GetChild();
        set => _root.SetChild(value);
    }

    /// <summary>True while the app is locked: messages only queue. Clearing it shows the next one.</summary>
    public bool IsSuspended
    {
        get => _suspended;
        set
        {
            if (_suspended == value) return;
            _suspended = value;
            if (!value && _visible is null) Next();
        }
    }

    public int PendingCount => _pending.Count;

    public void Show(string? title, string? message, InfoBarMessageType type, string? actionText = null, Action? action = null)
    {
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(message)) return;
        var hasAction = !string.IsNullOrEmpty(actionText) && action is not null;
        var item = new Message(title ?? string.Empty, message ?? string.Empty, type, hasAction ? actionText : null, hasAction ? action : null);

        if (_visible is { } visible && _pending.Count == 0 && !hasAction && visible.ActionText is null
            && visible.Title == item.Title && visible.Text == item.Text && visible.Type == item.Type)
        {
            Bar.RestartAutoDismiss();
            return;
        }

        if (_visible is not null || _suspended)
        {
            _pending.Enqueue(item);
            return;
        }
        Present(item);
    }

    /// <summary>Hides the visible message and shows the next.</summary>
    public void Dismiss()
    {
        Bar.IsOpen = false;
        Next();
    }

    public string Describe()
        => $"visible={(_visible is null ? "none" : Bar.Describe())} pending={_pending.Count} suspended={_suspended}";

    public void Dispose()
    {
        _pending.Clear();
        _visible = null;
        Bar.Dispose();
    }

    private void Present(Message message)
    {
        _visible = message;
        Bar.Severity = message.Type switch
        {
            InfoBarMessageType.Success => WinoInfoBarSeverity.Success,
            InfoBarMessageType.Warning => WinoInfoBarSeverity.Warning,
            InfoBarMessageType.Error => WinoInfoBarSeverity.Error,
            _ => WinoInfoBarSeverity.Informational,
        };
        Bar.Title = message.Title;
        Bar.Message = message.Text;
        Bar.ActionTitle = message.ActionText;
        _card.SetVisible(true);
        Bar.IsOpen = true;
        Bar.Root.Announce($"{message.Title} {message.Text}".Trim(), Gtk.AccessibleAnnouncementPriority.Medium);
    }

    private void Next()
    {
        _visible = null;
        _card.SetVisible(false);
        if (_suspended || !_pending.TryDequeue(out var next)) return;
        Present(next);
    }

    private void RunAction()
    {
        var action = _visible?.Action;
        Bar.IsOpen = false;
        // The action runs after the next message is shown, so a message it raises queues behind it instead of replacing it.
        Next();
        if (action is null) return;
        try { action(); }
        catch (Exception exception) { Serilog.Log.Error(exception, "An info bar action failed."); }
    }
}
