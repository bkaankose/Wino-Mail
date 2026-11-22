using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels.Common;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>
/// The base of every Settings page: a <see cref="WinoSettingsPage"/> filled by <see cref="BuildPage"/> through
/// <see cref="Settings"/>, whose bindings belong to the view and are released with it. <see cref="BuildPage"/> runs once
/// when the page is shown, before the ViewModel is navigated to, so derived fields are already assigned.
/// </summary>
internal abstract class SettingsPageView<TViewModel> : WinoView<TViewModel> where TViewModel : CoreBaseViewModel
{
    protected SettingsPageView(TViewModel viewModel, WinoViewServices services) : base(viewModel, services)
    {
        Page = new WinoSettingsPage();
        Settings = new SettingsBinder(Bindings, Dispatcher, ReportError);
    }

    public sealed override Gtk.Widget Root => Page.Root;

    protected WinoSettingsPage Page { get; }

    protected SettingsBinder Settings { get; }

    protected abstract void BuildPage();

    protected WinoSettingsGroup AddGroup(string? title, params Gtk.Widget?[] rows) => Page.AddGroup(title, null, rows);

    protected sealed override Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        BuildPage();
        return NavigateAsync(mode, parameter);
    }

    /// <summary>Override for a ViewModel that navigates asynchronously (<c>InitializeNavigationAsync</c>).</summary>
    protected virtual Task NavigateAsync(NavigationMode mode, object? parameter)
    {
        ViewModel.OnNavigatedTo(mode, parameter);
        return Task.CompletedTask;
    }
}
