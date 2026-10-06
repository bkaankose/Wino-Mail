using AppKit;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.AppKit;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.Messages;

namespace Wino.Mail.MacOS.Views.Mail;

public sealed class MailListPageViewController(MailListPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : WinoViewController<MailListPageViewModel>(viewModel, dispatcher, logger)
{
    public override void LoadView()
    {
        var heading = NSTextField.CreateLabel(string.Empty);
        BindText(heading, nameof(ViewModel.ActiveFolder), vm => vm.ActiveFolder?.FolderName ?? string.Empty);
        var messages = Bindings.Own(new WinoCollectionView<MailItemViewModel>(message => message.MailCopy.UniqueId.ToString(),
            message => $"{message.FromName} — {message.MailCopy.Subject}", Dispatcher, ReportError));
        ViewModel.MailCollection.CoreDispatcher = Dispatcher;
        messages.Bind(ViewModel.MailCollection.Items);
        messages.HeightAnchor.ConstraintGreaterThanOrEqualTo(400).Active = true;
        View = new MailListPage(heading, messages);
    }
    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        ViewModel.OnNavigatedTo(mode, parameter!);
        if (parameter is NavigateMailFolderEventArgs folder)
        {
            WeakReferenceMessenger.Default.Send(new ActiveMailFolderChangedEvent(folder.BaseFolderMenuItem, folder.FolderInitLoadAwaitTask));
            await ViewModel.WaitForCurrentFolderInitializationAsync();
        }
    }

    protected override Task DeactivateAsync() => ViewModel.DeactivateAsync(NavigationMode.New, null!);
}
