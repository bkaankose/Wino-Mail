using Android.Views;
using AndroidX.AppCompat.App;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.ViewModels.Lifecycle;
using Wino.Messaging.UI;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.Extras;

/// <summary>
/// Opens What's New on Android (iOS and Mac WhatsNewPresenter, Windows ShellWindow) from the About page
/// (<see cref="WhatsNewOpenRequested"/>): <see cref="WhatsNewView"/> over a fresh <see cref="WhatsNewPageViewModel"/> in a
/// Material full-screen dialog with Close. Opening marks the running version as seen, as on Windows. Closing the dialog
/// releases the page.
/// </summary>
public sealed class AndroidWhatsNewPresenter : IRecipient<WhatsNewOpenRequested>
{
    private readonly IServiceProvider _services;
    private readonly IDispatcher _dispatcher;
    private readonly IWinoLogger _logger;
    private readonly IWhatsNewService _whatsNewService;
    private readonly AndroidActivityTracker _activities;
    private AppCompatDialog? _dialog;

    public AndroidWhatsNewPresenter(IServiceProvider services, IDispatcher dispatcher, IWinoLogger logger, IWhatsNewService whatsNewService, AndroidActivityTracker activities)
    {
        _services = services;
        _dispatcher = dispatcher;
        _logger = logger;
        _whatsNewService = whatsNewService;
        _activities = activities;
        WeakReferenceMessenger.Default.Register(this);
    }

    public bool IsOpen => _dialog?.IsShowing == true;

    public void Receive(WhatsNewOpenRequested message) => _ = ShowAsync();

    public async Task ShowAsync()
    {
        try
        {
            WhatsNewPageViewModel? viewModel = null;
            await _dispatcher.ExecuteOnUIThread(() =>
            {
                if (IsOpen || _activities.Current is not AppCompatActivity { IsFinishing: false } activity) return;
                viewModel = _services.GetRequiredService<WhatsNewPageViewModel>();
                var view = new WhatsNewView(activity, viewModel, _dispatcher);
                var dialog = new AppCompatDialog(activity, Resource.Style.Theme_Wino_FullScreenDialog);
                dialog.SetContentView(view);
                dialog.Window?.SetLayout(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
                view.CloseRequested += (_, _) => dialog.Dismiss();
                dialog.DismissEvent += (_, _) =>
                {
                    view.Detach();
                    viewModel.OnNavigatedFrom(NavigationMode.Back, null);
                    if (ReferenceEquals(_dialog, dialog)) _dialog = null;
                };
                _dialog = dialog;
                dialog.Show();
                view.Attach();
            });
            if (viewModel is null) return;
            viewModel.OnNavigatedTo(NavigationMode.New, null);
            _whatsNewService.MarkOpenedForCurrentVersion();
            WeakReferenceMessenger.Default.Send(new WhatsNewOpened());
        }
        catch (Exception exception)
        {
            _logger.CaptureException(exception, nameof(AndroidWhatsNewPresenter));
        }
    }

    /// <summary>Closes the dialog (debug commands, the activity going away).</summary>
    public void Close()
    {
        var dialog = _dialog;
        _dialog = null;
        if (dialog?.IsShowing == true) dialog.Dismiss();
    }
}

/// <summary>Creates the What's New presenter once services are ready, so it answers the About page (iOS ShellExtrasLifecycleHook).</summary>
internal sealed class WhatsNewLifecycleHook(IServiceProvider services) : IApplicationLifecycleHook
{
    public Task ServicesReadyAsync()
    {
        services.GetRequiredService<AndroidWhatsNewPresenter>();
        return Task.CompletedTask;
    }

    public Task StoppingAsync(bool runtimeStarted) => Task.CompletedTask;
}
