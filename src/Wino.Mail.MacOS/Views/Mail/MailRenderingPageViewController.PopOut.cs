using AppKit;
using CommunityToolkit.Mvvm.Messaging;
using CoreGraphics;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels.Models;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>
/// Pop-out reader (Windows MailRenderingPage as an IPopoutClient through HostedContentPopoutCoordinator):
/// the live reader moves from the reading pane into its own 820×640 window titled with the subject;
/// the pane goes back to its idle page and the list selection clears. A second request for the same
/// message only brings its window forward. A popped-out reader never takes the pane's selection again,
/// closes its window when its message goes away, and asks the pane to pop out the composer that its
/// Reply, Reply all or Forward opens (Windows PopOutNextNavigation). Closing the window releases it.
/// </summary>
public sealed partial class MailRenderingPageViewController : IReadingPaneChild
{
    private static readonly Dictionary<Guid, MailRenderingPageViewController> PoppedOut = new();
    private IReadingPaneHost? _paneHost;
    private NSWindow? _popOutWindow;
    private Guid? _popOutKey;

    public IReadingPaneHost? PaneHost
    {
        get => _paneHost;
        set
        {
            // A popped-out reader keeps its host so Reply can hand the composer over.
            if (value is not null || _popOutWindow is null) _paneHost = value;
            if (ViewLoaded) UpdateCommandBar();
        }
    }

    /// <summary>Windows SupportsPopOut: additional windows are available and this reader still lives in the pane.</summary>
    private bool SupportsPopOut => ViewModel.PlatformCapabilities.AdditionalWindows && _popOutWindow is null && _paneHost is { IsAvailable: true };

    /// <summary>Whether this reader lives in its own window.</summary>
    internal bool IsPoppedOut => _popOutWindow is not null;

    /// <summary>Adds the Pop out command at the end of the primary commands while the reader can leave the pane.</summary>
    private void AppendPopOutCommand(List<MailReaderCommand?> commands)
    {
        if (!SupportsPopOut || ViewModel.CurrentMailFileId is null) return;
        commands.Add(null);
        commands.Add(new MailReaderCommand(WinoIconGlyph.OpenInNewWindow, Translator.Buttons_PopOut, () => Observe(PopOutAsync())));
    }

    /// <summary>Moves this reader into its own window. Returns false when it cannot leave the pane.</summary>
    internal async Task<bool> PopOutAsync()
    {
        var host = _paneHost;
        var key = ViewModel.CurrentMailFileId;
        if (!SupportsPopOut || host is null || key is null) return false;

        // The same message already has a window: bring it forward instead of opening another.
        if (PoppedOut.TryGetValue(key.Value, out var existing) && existing._popOutWindow is { } existingWindow)
        {
            await Dispatcher.ExecuteOnUIThread(() => existingWindow.MakeKeyAndOrderFront(null));
            return true;
        }

        await host.PopOutAsync(this);
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            var window = new NSWindow(new CGRect(0, 0, 820, 640),
                NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Resizable | NSWindowStyle.Miniaturizable,
                NSBackingStore.Buffered, false)
            {
                ReleasedWhenClosed = false,
                Title = string.IsNullOrWhiteSpace(ViewModel.Subject) ? Translator.MailItemNoSubject : ViewModel.Subject,
                ContentMinSize = new CGSize(640, 480),
                Identifier = $"mail-rendering-{key.Value:N}"
            };
            window.ContentViewController = this;
            window.SetContentSize(new CGSize(820, 640));
            window.Center();
            window.WillClose += PopOutWindowWillClose;
            _popOutWindow = window;
            _popOutKey = key;
            PoppedOut[key.Value] = this;
            UpdateCommandBar();
            window.MakeKeyAndOrderFront(null);
        });
        // Windows clears the list selection once the reader leaves the pane.
        WeakReferenceMessenger.Default.Send(new Wino.Messaging.Client.Mails.ClearMailSelectionsRequested());
        return true;
    }

    /// <summary>Closes the window when the reader is popped out; false when it lives in the pane.</summary>
    private bool ClosePopOutWindow()
    {
        if (_popOutWindow is not { } window) return false;
        window.Close();
        return true;
    }

    /// <summary>Keeps the window title on the shown subject.</summary>
    private void UpdatePopOutTitle(string? subject)
    {
        if (_popOutWindow is { } window) window.Title = string.IsNullOrWhiteSpace(subject) ? Translator.MailItemNoSubject : subject;
    }

    /// <summary>Reply, Reply all or Forward from a popped-out reader: the pane pops the new composer out too.</summary>
    private void ComposeRequested(object? sender, ComposeDraftRequestedEventArgs args)
    {
        if (_popOutWindow is null) return;
        _paneHost?.PopOutNextComposer(args.DraftUniqueId);
    }

    private async void PopOutWindowWillClose(object? sender, EventArgs args)
    {
        var window = _popOutWindow;
        _popOutWindow = null;
        if (window is not null) window.WillClose -= PopOutWindowWillClose;
        if (_popOutKey is { } key && PoppedOut.TryGetValue(key, out var owner) && ReferenceEquals(owner, this)) PoppedOut.Remove(key);
        _popOutKey = null;
        _paneHost = null;
        try { await ReleaseAsync(); }
        catch (Exception exception) { ReportError(exception); }
        finally
        {
            if (window is not null) window.ContentViewController = null!;
            Dispose();
        }
    }

#if DEBUG
    /// <summary>Debug bridge: pops out the reader that shows the selected message.</summary>
    private void RegisterPopOutDebugCommands()
    {
        Infrastructure.MacDebugBridge.Register("reader-popout", async _ =>
        {
            if (_popOutWindow is not null) return "already popped out";
            if (!ViewModel.PlatformCapabilities.AdditionalWindows) return "additional windows unavailable";
            if (_paneHost is not { IsAvailable: true }) return "no reading pane host";
            if (!await PopOutAsync()) return "pop out refused (no message loaded?)";
            await Task.Delay(300);
            string result = string.Empty;
            await Dispatcher.ExecuteOnUIThread(() => result = $"window='{_popOutWindow?.Title}' size={_popOutWindow?.ContentView?.Frame.Size} open={PoppedOut.Count}");
            return result;
        });
    }
#endif
}
