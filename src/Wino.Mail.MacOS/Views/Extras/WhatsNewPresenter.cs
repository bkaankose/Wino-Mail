using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Messaging.UI;

namespace Wino.Mail.MacOS.Views.Extras;

/// <summary>
/// Opens the What's New window from the title bar, the About page (<see cref="WhatsNewOpenRequested"/>)
/// and the debug bridge. Opening marks the running version as seen, as on Windows, and the shell
/// hides its button on <see cref="WhatsNewOpened"/>.
/// </summary>
public sealed class WhatsNewPresenter : IWhatsNewPresenter, IRecipient<WhatsNewOpenRequested>
{
    private readonly IServiceProvider _services;
    private readonly IDispatcher _dispatcher;
    private readonly IWinoLogger _logger;
    private readonly IWhatsNewService _whatsNewService;
    private WhatsNewWindowController? _window;
    private WhatsNewPageViewController? _page;

    public WhatsNewPresenter(IServiceProvider services, IDispatcher dispatcher, IWinoLogger logger, IWhatsNewService whatsNewService)
    {
        _services = services;
        _dispatcher = dispatcher;
        _logger = logger;
        _whatsNewService = whatsNewService;
        WeakReferenceMessenger.Default.Register(this);
#if DEBUG
        ShellExtrasDebug.Services ??= services;
#endif
    }

    public void Receive(WhatsNewOpenRequested message) => _ = ShowAsync();

    public async Task ShowAsync()
    {
        try
        {
            WhatsNewPageViewController? page = null;
            await _dispatcher.ExecuteOnUIThread(() =>
            {
                _window ??= CreateWindow();
                if (_page is null)
                {
                    page = _page = _services.GetRequiredService<WhatsNewPageViewController>();
                    _window.SetPage(page);
                }
                _window.Present();
            });
            if (page is not null) await page.ActivateAsync(NavigationMode.New, null);
            _whatsNewService.MarkOpenedForCurrentVersion();
            WeakReferenceMessenger.Default.Send(new WhatsNewOpened());
        }
        catch (Exception exception)
        {
            _logger.CaptureException(exception, nameof(WhatsNewPresenter));
        }
    }

    private WhatsNewWindowController CreateWindow()
    {
        var window = new WhatsNewWindowController();
        window.Closing += (_, _) =>
        {
            var page = _page;
            _page = null;
            window.ClearPage();
            if (page is not null) _ = ReleaseAsync(page);
        };
        return window;
    }

    private async Task ReleaseAsync(WhatsNewPageViewController page)
    {
        try { await page.ReleaseAsync(); }
        catch (Exception exception) { _logger.CaptureException(exception, nameof(WhatsNewPresenter)); }
        finally { await _dispatcher.ExecuteOnUIThread(page.Dispose); }
    }
}
