using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Messaging.Client.Mails;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Models;
using Wino.Platform.Linux.Services;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>
/// Pop out (Windows <c>HostedContentPopoutCoordinator</c>, macOS <c>PopOutAsync</c>): the live reader moves into its own
/// window and the pane goes idle; the same window also hosts an opened <c>.eml</c> file (<see cref="EmlReaderWindow"/>).
/// </summary>
internal sealed partial class MailReaderView
{
    private SecondaryWindow? _window;
    private string? _windowKey;
    private Func<Task>? _windowClosed;

    internal bool SupportsPopOut => HostKind == ReaderHostKind.Pane && !IsReleased && ViewModel.PlatformCapabilities.AdditionalWindows && ViewModel.CurrentMailFileId is not null;

    internal string? WindowKey => _windowKey;

    partial void InitializePopOut()
    {
        Bind.Bind(nameof(MailRenderingPageViewModel.Subject), static vm => vm.Subject, subject =>
        {
            if (_window is { } window) window.Title = TitleFor(subject);
        });
        var registry = _provider.GetRequiredService<ReaderWindowRegistry>();
        EventHandler<ComposeDraftRequestedEventArgs> composeRequested = (_, args) =>
        {
            if (HostKind == ReaderHostKind.PopOut) RunOnUI(() => registry.RequestComposerPopOut(args.DraftUniqueId));
        };
        var viewModel = ViewModel;
        viewModel.ComposeRequested += composeRequested;
        Bindings.Own(new ActionDisposable(() => viewModel.ComposeRequested -= composeRequested));
#if DEBUG
        WhenDescribing(() => $"popout supports={SupportsPopOut} window={_windowKey}");
#endif
    }

    partial void CollectPopOutCommand(ref MailReaderCommand? command)
    {
        if (!SupportsPopOut) return;
        command = new MailReaderCommand("PopOut", WinoIconGlyph.OpenInNewWindow, Translator.Buttons_PopOut, _ => Observe(PopOutAsync()),
            Accelerator: LinuxKeymap.AcceleratorFor(LinuxKeymap.OpenInWindow));
    }

    partial void HandleCloseRequested(ref bool handled)
    {
        if (_window is not { } window || HostKind != ReaderHostKind.PopOut) return;
        window.Close();
        handled = true;
    }

    internal Task<bool> PopOutAsync() => Task.FromResult(PopOut());

    private bool PopOut()
    {
        if (!SupportsPopOut || ViewModel.CurrentMailFileId is not { } fileId) return false;
        var registry = _provider.GetRequiredService<ReaderWindowRegistry>();
        var key = ReaderWindowRegistry.MailKey(fileId);
        if (registry.TryPresent(key)) return true;
        if (Gio.Application.GetDefault() is not Gtk.Application application) return false;
        var navigation = _provider.GetRequiredService<GtkNavigationService>();
        if (!navigation.DetachFromReadingPane(this)) return false;
        if (Root.GetParent() is not null)
        {
            Serilog.Log.Error("The reader is still in the reading pane after it was detached; it is not popped out.");
            return false;
        }

        HostKind = ReaderHostKind.PopOut;
        var window = new SecondaryWindow(application, ReaderWindowRegistry.Kind, Root, TitleFor(ViewModel.Subject),
            ReaderWindowRegistry.DefaultWidth, ReaderWindowRegistry.DefaultHeight, registry.Windows.Sizes);
        AttachWindow(window, key, null);
        var shortcuts = _provider.GetRequiredService<IKeyboardShortcutService>();
        var platform = _provider.GetRequiredService<LinuxShortcutPlatformService>();
        var self = new WeakReference<MailReaderView>(this);
        window.AttachShortcuts(owner => new ShortcutRouter(owner, shortcuts, platform, new ShortcutRouterContext(
            Mode: static () => WinoApplicationMode.Mail,
            RootViewModel: static () => null,
            SurfaceViewModel: () => self.TryGetTarget(out var view) ? view.ViewModel : null,
            ModeHook: static _ => Task.CompletedTask,
            IsPopOut: true)
        {
            RunFixed = action => self.TryGetTarget(out var view) && view.TryHandlePopOutKey(action)
        }, ReportError));
        window.Present();
        FocusBody();
        UpdateCommandBar();

        // Windows clears the list selection once the reader left the pane.
        navigation.Navigate(WinoPage.IdlePage, null, NavigationReferenceFrame.RenderingFrame);
        Messenger.Send(new ClearMailSelectionsRequested());
        return true;
    }

    /// <summary>Puts this reader into <paramref name="window"/>; its close releases the reader, then runs <paramref name="closed"/>.</summary>
    internal void AttachWindow(SecondaryWindow window, string key, Func<Task>? closed)
    {
        _window = window;
        _windowKey = key;
        _windowClosed = closed;
        _provider.GetRequiredService<ReaderWindowRegistry>().Windows.Add(key, window);
        window.Closing = CloseWindowAsync;
    }

    private async Task CloseWindowAsync()
    {
        var closed = _windowClosed;
        _window = null;
        _windowClosed = null;
        await ReleaseAsync();
        if (closed is not null) await closed();
    }

    // A new window focuses its first focusable widget, the selectable subject, which then shows fully selected.
    internal void FocusBody()
    {
        if (_window is { } window && _reader is { } reader) window.Window.SetFocus(reader.View);
        _subject.SelectRegion(0, 0);
    }

    private static string TitleFor(string? subject) => string.IsNullOrWhiteSpace(subject) ? Translator.MailItemNoSubject : subject;
}
