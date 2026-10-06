using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels;

namespace Wino.Mail.AppKit.Poc.ViewModels;

public partial class AboutPageViewModel : CoreBaseViewModel, IDisposable
{
    private readonly IPocDialogService _dialogService;
    private int _navigationCount;
    private bool _disposed;

    [ObservableProperty]
    private string lifecycleStatus = "Created";

    [ObservableProperty]
    private string pageLoadedStatus = "Not loaded";

    public AboutPageViewModel(IPocDialogService dialogService, IDispatcher dispatcher)
    {
        _dialogService = dialogService;
        Dispatcher = dispatcher;
    }

    public override void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);
        _navigationCount++;
        LifecycleStatus = $"OnNavigatedTo: {mode} (visit {_navigationCount})";
    }

    public override void OnNavigatedFrom(NavigationMode mode, object parameters)
    {
        base.OnNavigatedFrom(mode, parameters);
        LifecycleStatus = $"OnNavigatedFrom: {mode}";
    }

    public override void OnPageLoaded() => PageLoadedStatus = "OnPageLoaded called";

    [RelayCommand]
    private async Task ShowDialogAsync()
    {
        LifecycleStatus = "Dialog command executed";
        await _dialogService.ShowMessageAsync(
            Translator.GetTranslatedString("GeneralTitle_Info"),
            LifecycleStatus);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        LifecycleStatus = "Disposed";
    }
}
