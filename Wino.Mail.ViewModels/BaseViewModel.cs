using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.Navigation;
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels
{
    public class BaseViewModel : ObservableRecipient,
        INavigationAware,
        IRecipient<AccountCreatedMessage>,
        IRecipient<AccountRemovedMessage>,
        IRecipient<AccountUpdatedMessage>,
        IRecipient<MailAddedMessage>,
        IRecipient<MailRemovedMessage>,
        IRecipient<MailUpdatedMessage>,
        IRecipient<MailDownloadedMessage>,
        IRecipient<DraftCreated>,
        IRecipient<DraftFailed>,
        IRecipient<DraftMapped>,
        IRecipient<FolderRenamed>,
        IRecipient<FolderSynchronizationEnabled>
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

        protected IDialogService DialogService { get; }

        public BaseViewModel(IDialogService dialogService) => DialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));

        public async Task ExecuteUIThread(Action action) => await Dispatcher?.ExecuteOnUIThread(action);

        public virtual void OnNavigatedTo(NavigationMode mode, object parameters) { IsActive = true; }

        public virtual void OnNavigatedFrom(NavigationMode mode, object parameters) { IsActive = false; }

        protected virtual void OnDispatcherAssigned() { }

        protected virtual void OnMailAdded(MailCopy addedMail) { }
        protected virtual void OnMailRemoved(MailCopy removedMail) { }
        protected virtual void OnMailUpdated(MailCopy updatedMail) { }
        protected virtual void OnMailDownloaded(MailCopy downloadedMail) { }

        protected virtual void OnAccountCreated(MailAccount createdAccount) { }
        protected virtual void OnAccountRemoved(MailAccount removedAccount) { }
        protected virtual void OnAccountUpdated(MailAccount updatedAccount) { }


        protected virtual void OnDraftCreated(MailCopy draftMail, MailAccount account) { }
        protected virtual void OnDraftFailed(MailCopy draftMail, MailAccount account) { }
        protected virtual void OnDraftMapped(string localDraftCopyId, string remoteDraftCopyId) { }
        protected virtual void OnFolderRenamed(IMailItemFolder mailItemFolder) { }
        protected virtual void OnFolderSynchronizationEnabled(IMailItemFolder mailItemFolder) { }

        public void ReportUIChange<TMessage>(TMessage message) where TMessage : class, IUIMessage => Messenger.Send(message);
        void IRecipient<AccountCreatedMessage>.Receive(AccountCreatedMessage message) => OnAccountCreated(message.Account);
        void IRecipient<AccountRemovedMessage>.Receive(AccountRemovedMessage message) => OnAccountRemoved(message.Account);
        void IRecipient<AccountUpdatedMessage>.Receive(AccountUpdatedMessage message) => OnAccountUpdated(message.Account);


        void IRecipient<MailAddedMessage>.Receive(MailAddedMessage message) => OnMailAdded(message.AddedMail);
        void IRecipient<MailRemovedMessage>.Receive(MailRemovedMessage message) => OnMailRemoved(message.RemovedMail);
        void IRecipient<MailUpdatedMessage>.Receive(MailUpdatedMessage message) => OnMailUpdated(message.UpdatedMail);
        void IRecipient<MailDownloadedMessage>.Receive(MailDownloadedMessage message) => OnMailDownloaded(message.DownloadedMail);

        void IRecipient<DraftMapped>.Receive(DraftMapped message) => OnDraftMapped(message.LocalDraftCopyId, message.RemoteDraftCopyId);
        void IRecipient<DraftFailed>.Receive(DraftFailed message) => OnDraftFailed(message.DraftMail, message.Account);
        void IRecipient<DraftCreated>.Receive(DraftCreated message) => OnDraftCreated(message.DraftMail, message.Account);

        void IRecipient<FolderRenamed>.Receive(FolderRenamed message) => OnFolderRenamed(message.MailItemFolder);
        void IRecipient<FolderSynchronizationEnabled>.Receive(FolderSynchronizationEnabled message) => OnFolderSynchronizationEnabled(message.MailItemFolder);
    }
}
