using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Messaging.UI;

namespace Wino.Core.ViewModels;

public class CoreBaseViewModel : ObservableRecipient,
    INavigationAware,
    IRecipient<AccountCreatedMessage>,
    IRecipient<AccountRemovedMessage>,
    IRecipient<AccountUpdatedMessage>
{
    private IDispatcher _dispatcher;
    public IDispatcher Dispatcher
    {
        get
        {
            return _dispatcher;
        }
        set
        {
            _dispatcher = value;

            if (value != null)
            {
                OnDispatcherAssigned();
            }
        }
    }

    public virtual void OnNavigatedTo(NavigationMode mode, object parameters) 
    { 
        RegisterRecipients();
    }

    public virtual void OnNavigatedFrom(NavigationMode mode, object parameters) 
    { 
        UnregisterRecipients();
    }

    public virtual void OnPageLoaded() { }

    public async Task ExecuteUIThread(Action action) => await Dispatcher?.ExecuteOnUIThread(action);
    public void ReportUIChange<TMessage>(TMessage message) where TMessage : class, IUIMessage => Messenger.Send(message);

    protected virtual void OnDispatcherAssigned() { }

    /// <summary>
    /// Register message recipients for this view model. Override to register specific message types.
    /// </summary>
    protected virtual void RegisterRecipients() { }

    /// <summary>
    /// Unregister message recipients for this view model. Override to unregister specific message types.
    /// </summary>
    protected virtual void UnregisterRecipients() { }

    protected virtual void OnAccountCreated(MailAccount createdAccount) { }
    protected virtual void OnAccountRemoved(MailAccount removedAccount) { }
    protected virtual void OnAccountUpdated(MailAccount updatedAccount) { }

    void IRecipient<AccountCreatedMessage>.Receive(AccountCreatedMessage message) => OnAccountCreated(message.Account);
    void IRecipient<AccountRemovedMessage>.Receive(AccountRemovedMessage message) => OnAccountRemoved(message.Account);
    void IRecipient<AccountUpdatedMessage>.Receive(AccountUpdatedMessage message) => OnAccountUpdated(message.Account);
}
