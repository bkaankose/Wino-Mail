using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Linux.Views;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>
/// The shared onboarding secondary surface, as the macOS <c>WelcomeWindow</c>: one window, each page replaces the
/// previous one in it. The user's close tells the coordinator; <see cref="CloseAsync"/> with <c>closedByUser: false</c>
/// closes it for the app. <see cref="ShowAsync"/> runs on the main thread (the navigation service calls it there).
/// </summary>
internal sealed class GtkSecondaryWindowPresenter(IServiceProvider services, IDispatcher dispatcher, WindowManager windows) : ISecondaryWindowPresenter
{
    public const string Key = "onboarding";

    private SecondaryWindow? _window;
    private IWinoView? _view;

    public bool IsShown => _window is { IsClosing: false };

    public async Task ShowAsync(WinoPage page, IWinoView view, object? parameter, NavigationMode mode, Action committed)
    {
        var previous = _view;
        if (_window is not { IsClosing: false } window)
        {
            if (Gio.Application.GetDefault() is not Gtk.Application application)
                throw new InvalidOperationException("The secondary window needs the GTK application.");
            window = new SecondaryWindow(application, Key, view.Root, Title(page), 900, 640, windows.Sizes);
            var created = window;
            window.Closing = () => OnClosingAsync(created);
            windows.Add(Key, window);
            _window = window;
        }
        else
        {
            window.Content = view.Root;
            window.Title = Title(page);
        }
        window.Present();
        committed();
        _view = view;
        if (previous is not null && !ReferenceEquals(previous, view)) await previous.ReleaseAsync();
        await view.ActivateAsync(mode, parameter);
    }

    public async Task CloseAsync(bool closedByUser)
    {
        Task closed = Task.CompletedTask;
        await dispatcher.ExecuteOnUIThread(() =>
        {
            if (_window is not { } window) return;
            closed = window.Closed;
            if (!closedByUser) window.Close();
        });
        await closed;
    }

    /// <summary>The Window list name: the app on the welcome pages, "Add account" on the setup pages.</summary>
    private static string Title(WinoPage page)
        => page is WinoPage.WelcomePageV2 or WinoPage.WelcomeHostPage ? AppActions.ApplicationName : Translator.WelcomeWindow_AddAccountButton;

    private async Task OnClosingAsync(SecondaryWindow window)
    {
        if (!ReferenceEquals(_window, window)) return;
        var view = _view;
        _window = null;
        _view = null;
        if (window.ClosedByUser) services.GetRequiredService<GtkNavigationService>().OnSecondaryWindowClosedByUser();
        if (view is not null) await view.ReleaseAsync();
    }
}
