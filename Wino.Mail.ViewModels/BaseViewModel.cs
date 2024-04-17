using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Requests;
using Wino.Core.Requests;

namespace Wino.Mail.ViewModels
{
    public class BaseViewModel : ObservableRecipient,
        INavigationAware,
        IRecipient<AccountCreatedMessage>,
        IRecipient<AccountRemovedMessage>,
        IRecipient<AccountUpdatedMessage>,
        IRecipient<FolderAddedMessage>,
        IRecipient<FolderUpdatedMessage>,
        IRecipient<FolderRemovedMessage>,
        IRecipient<MailAddedMessage>,
        IRecipient<MailRemovedMessage>,
        IRecipient<MailUpdatedMessage>,
        IRecipient<MailDownloadedMessage>,
        IRecipient<DraftCreated>,
        IRecipient<DraftFailed>,
        IRecipient<DraftMapped>
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

        protected virtual void OnFolderAdded(MailItemFolder addedFolder, MailAccount account) { }
        protected virtual void OnFolderRemoved(MailItemFolder removedFolder, MailAccount account) { }
        protected virtual void OnFolderUpdated(MailItemFolder updatedFolder, MailAccount account) { }

        protected virtual void OnDraftCreated(MailCopy draftMail, MailAccount account) { }
        protected virtual void OnDraftFailed(MailCopy draftMail, MailAccount account) { }
        protected virtual void OnDraftMapped(string localDraftCopyId, string remoteDraftCopyId) { }

        public void ReportUIChange<TMessage>(TMessage message) where TMessage : class, IUIMessage
            => Messenger.Send(message);

        void IRecipient<AccountCreatedMessage>.Receive(AccountCreatedMessage message) => OnAccountCreated(message.Account);
        void IRecipient<AccountRemovedMessage>.Receive(AccountRemovedMessage message) => OnAccountRemoved(message.Account);
        void IRecipient<AccountUpdatedMessage>.Receive(AccountUpdatedMessage message) => OnAccountUpdated(message.Account);

        void IRecipient<FolderAddedMessage>.Receive(FolderAddedMessage message) => OnFolderAdded(message.AddedFolder, message.Account);
        void IRecipient<FolderUpdatedMessage>.Receive(FolderUpdatedMessage message) => OnFolderUpdated(message.UpdatedFolder, message.Account);
        void IRecipient<FolderRemovedMessage>.Receive(FolderRemovedMessage message) => OnFolderAdded(message.RemovedFolder, message.Account);

        void IRecipient<MailAddedMessage>.Receive(MailAddedMessage message) => OnMailAdded(message.AddedMail);
        void IRecipient<MailRemovedMessage>.Receive(MailRemovedMessage message) => OnMailRemoved(message.RemovedMail);
        void IRecipient<MailUpdatedMessage>.Receive(MailUpdatedMessage message) => OnMailUpdated(message.UpdatedMail);
        void IRecipient<MailDownloadedMessage>.Receive(MailDownloadedMessage message) => OnMailDownloaded(message.DownloadedMail);
        void IRecipient<DraftMapped>.Receive(DraftMapped message) => OnDraftMapped(message.LocalDraftCopyId, message.RemoteDraftCopyId);
        void IRecipient<DraftFailed>.Receive(DraftFailed message) => OnDraftFailed(message.DraftMail, message.Account);
        void IRecipient<DraftCreated>.Receive(DraftCreated message) => OnDraftCreated(message.DraftMail, message.Account);
    }
}
