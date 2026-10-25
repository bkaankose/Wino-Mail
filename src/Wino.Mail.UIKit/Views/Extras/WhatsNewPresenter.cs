using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using UIKit;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Messaging.UI;

namespace Wino.Mail.UIKit.Views.Extras;

/// <summary>
/// Opens What's New (Mac WhatsNewPresenter) from the shell button, the About page (<see cref="WhatsNewOpenRequested"/>)
/// and the debug bridge: the page in a sheet with Done (page sheet on iPhone, form sheet on iPad). Opening marks the
/// running version as seen, as on Windows, and the shell hides its button on <see cref="WhatsNewOpened"/>. Closing the
/// sheet releases the page.
/// </summary>
public sealed class WhatsNewPresenter : IUIKitWhatsNewPresenter, IRecipient<WhatsNewOpenRequested>
{
    private readonly IServiceProvider _services;
    private readonly IDispatcher _dispatcher;
    private readonly IWinoLogger _logger;
    private readonly IWhatsNewService _whatsNewService;
    private readonly IUIKitShellHost _host;
    private WhatsNewPageViewController? _page;
    private UINavigationController? _sheet;

    public WhatsNewPresenter(IServiceProvider services, IDispatcher dispatcher, IWinoLogger logger, IWhatsNewService whatsNewService, IUIKitShellHost host)
    {
        _services = services;
        _dispatcher = dispatcher;
        _logger = logger;
        _whatsNewService = whatsNewService;
        _host = host;
        WeakReferenceMessenger.Default.Register(this);
    }

    public void Receive(WhatsNewOpenRequested message) => _ = ShowAsync();

    public async Task ShowAsync()
    {
        try
        {
            // A sheet dismissed outside the presenter (no swipe report) is gone: release it and present a new one.
            bool stale = false;
            await _dispatcher.ExecuteOnUIThread(() => stale = _sheet is { PresentingViewController: null, IsBeingPresented: false });
            if (stale) await ReleaseAsync();

            WhatsNewPageViewController? page = null;
            await _dispatcher.ExecuteOnUIThread(() =>
            {
                if (_page is not null || _host.TopViewController is null) return;
                page = _page = Infrastructure.PageScopes.Resolve<WhatsNewPageViewController>(_services);
                page.NavigationItem.RightBarButtonItem = new UIBarButtonItem(UIBarButtonSystemItem.Done, (_, _) => _ = CloseAsync());
                // Settings-style sheets use FullScreenSheet on iPhone; What's New stays a dismissible page sheet on both.
                _sheet = _host.PresentSheet(page, UIKitPageHost.Sheet, () => _ = ReleaseAsync());
                _sheet.NavigationBar.PrefersLargeTitles = false;
            });
            if (page is null) return;
            await page.ActivateAsync(NavigationMode.New, null);
            _whatsNewService.MarkOpenedForCurrentVersion();
            WeakReferenceMessenger.Default.Send(new WhatsNewOpened());
        }
        catch (Exception exception)
        {
            _logger.CaptureException(exception, nameof(WhatsNewPresenter));
        }
    }

    /// <summary>Done: dismisses the sheet and releases the page.</summary>
    public async Task CloseAsync()
    {
        if (_sheet is { } sheet)
        {
            try { await _host.DismissSheetAsync(sheet); }
            catch (Exception exception) { _logger.CaptureException(exception, nameof(WhatsNewPresenter)); }
        }
        await ReleaseAsync();
    }

    private async Task ReleaseAsync()
    {
        var page = _page;
        _page = null;
        _sheet = null;
        if (page is null) return;
        try { await page.ReleaseAsync(); }
        catch (Exception exception) { _logger.CaptureException(exception, nameof(WhatsNewPresenter)); }
    }

#if DEBUG
    internal WhatsNewPageViewController? Page => _page;
#endif
}
