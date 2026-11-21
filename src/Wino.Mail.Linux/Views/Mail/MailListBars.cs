using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.Common;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>
/// The bars around the mail list (macOS <c>MailListPageViewController.Bars</c>): the folder synchronization notice and
/// the "unread mail in Other" link under the header, and the undo and update bars floating over the bottom of the list.
/// </summary>
internal sealed class MailListBars : IDisposable
{
    private const int UpdateSeconds = 2;

    private readonly Gtk.Box _root;
    private readonly WinoInfoBar _sync;
    private readonly Gtk.Button _other;
    private readonly Gtk.Label _otherText;
    private readonly WinoInfoBar _undo;
    private readonly WinoInfoBar _update;
    private readonly List<IDisposable> _subscriptions = [];
    private bool _syncDismissed;
    private bool _syncWanted;
    private bool _disposed;

    public MailListBars()
    {
        WinoLiveObjects.Track(this);
        _root = Gtk.Box.New(Gtk.Orientation.Vertical, 0);

        _sync = new WinoInfoBar(WinoInfoBarSeverity.Informational, Translator.InfoBarTitle_SynchronizationDisabledFolder, Translator.InfoBarMessage_SynchronizationDisabledFolder)
        {
            ActionTitle = Translator.MacOS_MailList_EnableFolderSync,
            IsClosable = true,
            IsOpen = false,
        };
        _sync.Root.SetMarginStart(4);
        _sync.Root.SetMarginEnd(4);
        _sync.Root.SetMarginBottom(6);
        _sync.ActionInvoked += _ => EnableSyncRequested?.Invoke();
        _sync.Closed += (_, _) =>
        {
            _syncDismissed = true;
            ApplySync();
        };
        _root.Append(_sync.Root);

        _otherText = Gtk.Label.New(string.Empty);
        _otherText.SetWrap(true);
        _otherText.SetLines(2);
        _otherText.SetEllipsize(Pango.EllipsizeMode.End);
        _otherText.SetXalign(0);
        _otherText.AddCssClass("accent");
        _other = Gtk.Button.New();
        _other.AddCssClass("flat");
        _other.AddCssClass(WinoCssClass.Link);
        _other.SetChild(_otherText);
        _other.SetMarginStart(8);
        _other.SetMarginEnd(8);
        _other.SetMarginBottom(6);
        _other.SetVisible(false);
        _root.Append(_other);
        _subscriptions.Add(GtkSignals.OnClicked(_other, () => OtherInboxRequested?.Invoke()));

        Floating = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        Floating.SetVisible(false);
        _undo = Card(new WinoInfoBar { ActionTitle = Translator.UndoActions_Button, IsClosable = false, IsOpen = false });
        _undo.ActionInvoked += _ => UndoRequested?.Invoke();
        _undo.Closed += (_, _) =>
        {
            UpdateFloating();
            UndoDismissed?.Invoke();
        };
        _update = Card(new WinoInfoBar { IsClosable = false, IsOpen = false, AutoDismissInterval = TimeSpan.FromSeconds(UpdateSeconds) });
        _update.Closed += (_, _) =>
        {
            UpdateFloating();
            UpdateDismissed?.Invoke();
        };
    }

    /// <summary>Under the header: the sync notice and the Other link.</summary>
    public Gtk.Widget Root => _root;

    /// <summary>Over the bottom of the list: the undo and update cards.</summary>
    public Gtk.Box Floating { get; }

    public event Action? EnableSyncRequested;
    public event Action? OtherInboxRequested;
    public event Action? UndoRequested;

    /// <summary>The undo bar timed out.</summary>
    public event Action? UndoDismissed;

    public event Action? UpdateDismissed;

    public void SetSyncDisabled(bool visible)
    {
        _syncWanted = visible;
        ApplySync();
    }

    /// <summary>The folder changed: a closed notice may show again.</summary>
    public void ResetSyncDismissal()
    {
        _syncDismissed = false;
        ApplySync();
    }

    public void SetOtherInbox(bool visible, string text)
    {
        _other.SetVisible(visible);
        if (!visible) return;
        _otherText.SetText(text);
        WinoA11y.Label(_other, text);
    }

    /// <summary>Shows the undo bar, or restarts its countdown when it is already shown.</summary>
    public void ShowUndo(string title, InfoBarMessageType severity, int seconds)
    {
        _undo.Title = title;
        _undo.Severity = Map(severity);
        _undo.AutoDismissInterval = TimeSpan.FromSeconds(seconds);
        if (_undo.IsOpen) _undo.RestartAutoDismiss();
        else _undo.IsOpen = true;
        UpdateFloating();
    }

    public void HideUndo()
    {
        _undo.IsOpen = false;
        UpdateFloating();
    }

    /// <summary>A blank message shows nothing.</summary>
    public void ShowUpdate(string? title, string? message, InfoBarMessageType severity)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            HideUpdate();
            return;
        }
        _update.Title = title;
        _update.Message = message;
        _update.Severity = Map(severity);
        if (_update.IsOpen) _update.RestartAutoDismiss();
        else _update.IsOpen = true;
        UpdateFloating();
    }

    public void HideUpdate()
    {
        _update.IsOpen = false;
        UpdateFloating();
    }

#if DEBUG
    internal string DescribeState()
        => $"sync={_sync.IsOpen} sync_dismissed={_syncDismissed} other={_other.GetVisible()} other_text={_otherText.GetText()} "
            + $"undo={_undo.Describe()} update={_update.Describe()}";

    internal void DebugUndo() => UndoRequested?.Invoke();
#endif

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        _sync.Dispose();
        _undo.Dispose();
        _update.Dispose();
        EnableSyncRequested = null;
        OtherInboxRequested = null;
        UndoRequested = null;
        UndoDismissed = null;
        UpdateDismissed = null;
    }

    private WinoInfoBar Card(WinoInfoBar bar)
    {
        bar.Root.AddCssClass(WinoCssClass.InfoBarFloating);
        bar.Root.SetHalign(Gtk.Align.Fill);
        Floating.Append(bar.Root);
        return bar;
    }

    private void ApplySync() => _sync.IsOpen = _syncWanted && !_syncDismissed;

    // An empty floating box would still take the clicks of the rows under it.
    private void UpdateFloating() => Floating.SetVisible(_undo.IsOpen || _update.IsOpen);

    private static WinoInfoBarSeverity Map(InfoBarMessageType severity) => severity switch
    {
        InfoBarMessageType.Success => WinoInfoBarSeverity.Success,
        InfoBarMessageType.Warning => WinoInfoBarSeverity.Warning,
        InfoBarMessageType.Error => WinoInfoBarSeverity.Error,
        _ => WinoInfoBarSeverity.Informational,
    };
}
