using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Platform.Linux.Services;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Mail.Compose;

/// <summary>
/// Pop out (Mac <c>TogglePopOut</c>, Windows <c>HostedContentPopoutCoordinator</c>): the live composer — view, ViewModel and
/// editor web view — moves from the reading pane into its own window and back. Closing the window keeps the draft
/// (releasing saves it). A composer started from a popped-out reader opens popped out.
/// </summary>
internal sealed partial class ComposeView
{
    public const string WindowKind = "composer";
    private readonly Gtk.Button _popOutButton = WinoIcons.Button(WinoIconGlyph.OpenInNewWindow, Translator.Buttons_PopOut);
    private SecondaryWindow? _window;
    private string? _windowKey;
    private bool _docking;
    private bool _popOutRequested;

    internal SecondaryWindow? PopOutWindow => _window;
    internal string? PopOutWindowKey => _windowKey;

    internal bool SupportsPopOut => !IsReleased && _provider.GetService<IPlatformCapabilities>() is { AdditionalWindows: true } && DraftId is not null;

    private Guid? DraftId => ViewModel.CurrentMailDraftItem?.UniqueId is { } id && id != Guid.Empty ? id : null;

    public static string WindowKey(Guid draftUniqueId) => $"{WindowKind}:{draftUniqueId:N}";

    partial void InitializePopOut()
    {
        _popOutButton.AddCssClass("wino-editor-tool");
        _popOutButton.SetValign(Gtk.Align.Center);
        _popOutButton.SetTooltipText(Translator.Buttons_PopOut);
        PopOutSlot.Append(_popOutButton);
        Bindings.Own(GtkSignals.OnClicked(_popOutButton, () => Observe(_window is null ? PopOutAsync() : DockAsync())));
        Bind.Bind(nameof(ComposePageViewModel.Subject), static vm => vm.Subject, subject =>
        {
            if (_window is { } window) window.Title = TitleFor(subject);
        });
        Bind.Bind(nameof(ComposePageViewModel.CurrentMailDraftItem), static vm => vm.CurrentMailDraftItem, _ => DraftChanged());
    }

    partial void DeactivatePopOut()
    {
        PopOutSlot.Remove(_popOutButton);
    }

    partial void ClosePopOutWindow() => _window?.Close();

    private void DraftChanged()
    {
        UpdatePopOutButton();
        if (_window is not null || _popOutRequested || DraftId is not { } draftId) return;
        var windows = _provider.GetRequiredService<WindowManager>();
        // The same draft already has a window: show that one instead of a second composer in the pane.
        if (windows.TryPresent(WindowKey(draftId)))
        {
            _provider.GetRequiredService<GtkNavigationService>().Navigate(WinoPage.IdlePage, null, NavigationReferenceFrame.RenderingFrame);
            return;
        }
        if (!_provider.GetRequiredService<ReaderWindowRegistry>().TakeComposerPopOut(draftId)) return;
        _popOutRequested = true;
        MainLoop.Post(() => Observe(PopOutAsync()));
    }

    private void UpdatePopOutButton()
    {
        var docked = _window is null;
        WinoIcons.SetGlyph((Gtk.Label)(_popOutButton.GetChild() ?? throw new InvalidOperationException("The pop-out button has no glyph.")),
            WinoIcons.Glyph(docked ? WinoIconGlyph.OpenInNewWindow : WinoIconGlyph.PanelLeft));
        _popOutButton.SetVisible(docked ? SupportsPopOut : true);
    }

    internal async Task<bool> PopOutAsync()
    {
        if (_window is not null || !SupportsPopOut || DraftId is not { } draftId) return false;
        ClosePopovers();
        // The draft is saved before the move so a web process restart during it still finds the text (EditorRestarted).
        await SyncAllRecipientsAsync();
        await ViewModel.UpdateMimeChangesAsync();
        var popped = false;
        await Dispatcher.ExecuteOnUIThread(() => popped = PopOut(draftId));
        return popped;
    }

    private bool PopOut(Guid draftId)
    {
        if (IsReleased || _window is not null) return false;
        var windows = _provider.GetRequiredService<WindowManager>();
        var key = WindowKey(draftId);
        if (windows.TryPresent(key)) return true;
        if (Gio.Application.GetDefault() is not Gtk.Application application) return false;
        var navigation = _provider.GetRequiredService<GtkNavigationService>();
        if (!navigation.DetachFromReadingPane(this)) return false;
        if (Root.GetParent() is not null)
        {
            Serilog.Log.Error("The composer is still in the reading pane after it was detached; it is not popped out.");
            return false;
        }

        var window = _window = new SecondaryWindow(application, WindowKind, Root, TitleFor(ViewModel.Subject), 960, 720, windows.Sizes, 640, 480);
        _windowKey = key;
        IsPoppedOut = true;
        windows.Add(key, window);
        window.Closing = WindowClosingAsync;
        var shortcuts = _provider.GetRequiredService<IKeyboardShortcutService>();
        var platform = _provider.GetRequiredService<LinuxShortcutPlatformService>();
        var self = new WeakReference<ComposeView>(this);
        window.AttachShortcuts(owner => new ShortcutRouter(owner, shortcuts, platform, new ShortcutRouterContext(
            Mode: static () => WinoApplicationMode.Mail,
            RootViewModel: static () => null,
            SurfaceViewModel: () => self.TryGetTarget(out var view) ? view.ViewModel : null,
            ModeHook: static _ => Task.CompletedTask,
            IsPopOut: true)
        {
            RunFixed = action => self.TryGetTarget(out var view) && view.TryHandlePopOutKey(action)
        }, ReportError));
        UpdatePopOutButton();
        WatchWindowActivation();
        window.Present();
        Observe(ApplyInitialFocusAsync(true));
        return true;
    }

    internal async Task<bool> DockAsync()
    {
        var docked = false;
        await Dispatcher.ExecuteOnUIThread(() => docked = Dock());
        if (docked && Editor is { } editor && !_editorDisposed) await editor.FocusEditorAsync(true);
        return docked;
    }

    private bool Dock()
    {
        if (_window is not { } window || IsReleased) return false;
        ClosePopovers();
        _docking = true;
        window.Content = Adw.Bin.New();
        if (!_provider.GetRequiredService<GtkNavigationService>().AdoptIntoReadingPane(WinoPage.ComposePage, this))
        {
            window.Content = Root;
            _docking = false;
            return false;
        }
        _window = null;
        _windowKey = null;
        IsPoppedOut = false;
        window.Close();
        UpdatePopOutButton();
        WatchWindowActivation();
        return true;
    }

    private async Task WindowClosingAsync()
    {
        if (_docking)
        {
            _docking = false;
            return;
        }
        _window = null;
        _windowKey = null;
        // Closing the window keeps the draft: releasing saves it, nothing is discarded.
        await ReleaseAsync();
    }

    private void ClosePopovers()
    {
        CloseSuggestions();
        SignatureMenu?.OpenMenu?.Popdown();
        _templatePopover?.Popover.Popdown();
    }

    private static string TitleFor(string? subject) => string.IsNullOrWhiteSpace(subject) ? Translator.MailItemNoSubject : subject;
}
