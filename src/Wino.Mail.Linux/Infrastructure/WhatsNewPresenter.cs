using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Messaging.UI;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Linux.Views;
using Wino.Mail.Linux.Views.Extras;
using Wino.Mail.ViewModels.Lifecycle;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>
/// Opens the What's New window (macOS WhatsNewPresenter) from the header button, the primary menu and the About page
/// (<see cref="WhatsNewOpenRequested"/>), one window under the key <c>whatsnew</c>. Opening marks the running version
/// as seen and sends <see cref="WhatsNewOpened"/>, which hides the header button.
/// </summary>
internal sealed class WhatsNewPresenter : IWhatsNewPresenter, IApplicationLifecycleHook
{
    public const string Key = "whatsnew";
    private const int DefaultWidth = 880;
    private const int DefaultHeight = 640;

    private readonly IServiceProvider _services;
    private readonly IDispatcher _dispatcher;
    private readonly IWinoLogger _logger;
    private readonly IMessenger _messenger;
    private readonly IWhatsNewService _whatsNew;
    private readonly WindowManager _windows;
    private bool _opening;

    public WhatsNewPresenter(IServiceProvider services, IDispatcher dispatcher, IWinoLogger logger, IMessenger messenger,
        IWhatsNewService whatsNew, WindowManager windows)
    {
        _services = services;
        _dispatcher = dispatcher;
        _logger = logger;
        _messenger = messenger;
        _whatsNew = whatsNew;
        _windows = windows;
        // Typed registration: RegisterAll reflects over the interfaces, which Native AOT cannot keep.
        messenger.Register<WhatsNewPresenter, WhatsNewOpenRequested>(this, static (recipient, message) => _ = recipient.ShowAsync());
    }

    public Task ServicesReadyAsync() => Task.CompletedTask;

    public Task StoppingAsync(bool runtimeStarted)
    {
        _messenger.Unregister<WhatsNewOpenRequested>(this);
        return Task.CompletedTask;
    }

#if DEBUG
    public WhatsNewView? DebugView { get; private set; }
#endif

    public async Task ShowAsync()
    {
        try
        {
            if (!await OpenAsync()) return;
            _whatsNew.MarkOpenedForCurrentVersion();
            _messenger.Send(new WhatsNewOpened());
        }
        catch (Exception exception)
        {
            Serilog.Log.Error(exception, "What's New failed to open.");
            _logger.CaptureException(exception, nameof(WhatsNewPresenter));
        }
    }

    private async Task<bool> OpenAsync()
    {
        var presented = false;
        var busy = false;
        await _dispatcher.ExecuteOnUIThread(() =>
        {
            presented = _windows.TryPresent(Key);
            busy = !presented && _opening;
            if (!presented && !busy) _opening = true;
        });
        if (presented) return true;
        if (busy) return false;

        WhatsNewView? view = null;
        try
        {
            await _dispatcher.ExecuteOnUIThread(() => view = PageScopes.Create<WhatsNewView>(_services));
            if (view is not { } page) return false;
            await page.ActivateAsync(NavigationMode.New, null);

            var opened = false;
            await _dispatcher.ExecuteOnUIThread(() =>
            {
                if (Gio.Application.GetDefault() is not Gtk.Application application || _windows.Find(Key) is not null) return;
                var window = new SecondaryWindow(application, Key, page.Root, Translator.WhatsNew_WindowTitle, DefaultWidth, DefaultHeight, _windows.Sizes, 640, 480);
                var shown = page;
                window.Closing = async () =>
                {
#if DEBUG
                    if (ReferenceEquals(DebugView, shown)) DebugView = null;
#endif
                    await shown.ReleaseAsync();
                };
                _windows.Add(Key, window);
#if DEBUG
                DebugView = page;
#endif
                window.Present();
                opened = true;
            });
            if (!opened) await page.ReleaseAsync();
            return opened;
        }
        catch
        {
            if (view is not null) await view.ReleaseAsync();
            throw;
        }
        finally
        {
            await _dispatcher.ExecuteOnUIThread(() => _opening = false);
        }
    }
}
