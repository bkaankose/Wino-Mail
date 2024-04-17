using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Mail.ViewModels.Data
{
    /// <summary>
    /// Single view model for IMailItem representation.
    /// </summary>
    public partial class MailItemViewModel(MailCopy mailCopy) : ObservableObject, IMailItem
    {
        public MailCopy MailCopy { get; private set; } = mailCopy;

        public bool IsLocalDraft => !string.IsNullOrEmpty(DraftId) && DraftId.StartsWith(Constants.LocalDraftStartPrefix);

        public Guid UniqueId => ((IMailItem)MailCopy).UniqueId;
        public string ThreadId => ((IMailItem)MailCopy).ThreadId;
        public string MessageId => ((IMailItem)MailCopy).MessageId;
        public string FromName => ((IMailItem)MailCopy).FromName ?? FromAddress;
        public DateTime CreationDate => ((IMailItem)MailCopy).CreationDate;
        public string FromAddress => ((IMailItem)MailCopy).FromAddress;
        public bool HasAttachments => ((IMailItem)MailCopy).HasAttachments;
        public string References => ((IMailItem)MailCopy).References;
        public string InReplyTo => ((IMailItem)MailCopy).InReplyTo;

        [ObservableProperty]
        private bool isCustomFocused;

        [ObservableProperty]
        private bool isSelected;

        public bool IsFlagged
        {
            get => MailCopy.IsFlagged;
            set => SetProperty(MailCopy.IsFlagged, value, MailCopy, (u, n) => u.IsFlagged = n);
        }

        public bool IsFocused
        {
            get => MailCopy.IsFocused;
            set => SetProperty(MailCopy.IsFocused, value, MailCopy, (u, n) => u.IsFocused = n);
        }

        public bool IsRead
        {
            get => MailCopy.IsRead;
            set => SetProperty(MailCopy.IsRead, value, MailCopy, (u, n) => u.IsRead = n);
        }

        public bool IsDraft
        {
            get => MailCopy.IsDraft;
            set => SetProperty(MailCopy.IsDraft, value, MailCopy, (u, n) => u.IsDraft = n);
        }

        public string DraftId
        {
            get => MailCopy.DraftId;
            set => SetProperty(MailCopy.DraftId, value, MailCopy, (u, n) => u.DraftId = n);
        }

        public string Id
        {
            get => MailCopy.Id;
            set => SetProperty(MailCopy.Id, value, MailCopy, (u, n) => u.Id = n);
        }

        public string Subject
        {
            get => MailCopy.Subject;
            set => SetProperty(MailCopy.Subject, value, MailCopy, (u, n) => u.Subject = n);
        }

        public string PreviewText
        {
            get => MailCopy.PreviewText;
            set => SetProperty(MailCopy.PreviewText, value, MailCopy, (u, n) => u.PreviewText = n);
        }

        public MailItemFolder AssignedFolder => ((IMailItem)MailCopy).AssignedFolder;

        public MailAccount AssignedAccount => ((IMailItem)MailCopy).AssignedAccount;

        public Guid FileId => ((IMailItem)MailCopy).FileId;

        public void Update(MailCopy updatedMailItem)
        {
            MailCopy = updatedMailItem;

            // DEBUG
            //if (updatedMailItem.AssignedAccount == null || updatedMailItem.AssignedFolder == null)
            //    throw new Exception("Assigned account or folder is null.");

            OnPropertyChanged(nameof(IsRead));
            OnPropertyChanged(nameof(IsFocused));
            OnPropertyChanged(nameof(IsFlagged));
            OnPropertyChanged(nameof(IsDraft));
            OnPropertyChanged(nameof(DraftId));
            OnPropertyChanged(nameof(Subject));
            OnPropertyChanged(nameof(PreviewText));
            OnPropertyChanged(nameof(IsLocalDraft));
        }
    }
}
