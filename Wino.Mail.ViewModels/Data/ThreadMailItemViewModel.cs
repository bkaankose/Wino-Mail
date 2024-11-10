using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Mail.ViewModels.Data
{
    /// <summary>
    /// Thread mail item (multiple IMailItem) view model representation.
    /// </summary>
    public partial class ThreadMailItemViewModel : ObservableObject, IMailItemThread, IComparable<string>, IComparable<DateTime>
    {
        public ObservableCollection<IMailItem> ThreadItems => (MailItem as IMailItemThread)?.ThreadItems ?? [];
        public AccountContact SenderContact => ((IMailItemThread)MailItem).SenderContact;

        [ObservableProperty]
        private ThreadMailItem mailItem;

        [ObservableProperty]
        private bool isThreadExpanded;

        public ThreadMailItemViewModel(ThreadMailItem threadMailItem)
        {
            MailItem = new ThreadMailItem();

            // Local copies
            foreach (var item in threadMailItem.ThreadItems)
            {
                AddMailItemViewModel(item);
            }
        }

        public ThreadMailItem GetThreadMailItem() => MailItem;

        public IEnumerable<MailCopy> GetMailCopies()
            => ThreadItems.OfType<MailItemViewModel>().Select(a => a.MailCopy);

        public void AddMailItemViewModel(IMailItem mailItem)
        {
            if (mailItem == null) return;

            if (mailItem is MailCopy mailCopy)
                MailItem.AddThreadItem(new MailItemViewModel(mailCopy));
            else if (mailItem is MailItemViewModel mailItemViewModel)
                MailItem.AddThreadItem(mailItemViewModel);
            else
                Debugger.Break();
        }

        public bool HasUniqueId(Guid uniqueMailId)
            => ThreadItems.Any(a => a.UniqueId == uniqueMailId);

        public IMailItem GetItemById(Guid uniqueMailId)
            => ThreadItems.FirstOrDefault(a => a.UniqueId == uniqueMailId);

        public void RemoveCopyItem(IMailItem item)
        {
            MailCopy copyToRemove = null;

            if (item is MailItemViewModel mailItemViewModel)
                copyToRemove = mailItemViewModel.MailCopy;
            else if (item is MailCopy copyItem)
                copyToRemove = copyItem;

            var existedItem = ThreadItems.FirstOrDefault(a => a.Id == copyToRemove.Id);

            if (existedItem == null) return;

            ThreadItems.Remove(existedItem);

            NotifyPropertyChanges();
        }

        public void NotifyPropertyChanges()
        {
            // TODO
            // Stupid temporary fix for not updating UI.
            // This view model must be reworked with ThreadMailItem together.

            var current = MailItem;

            MailItem = null;
            MailItem = current;
        }

        public IMailItem LatestMailItem => ((IMailItemThread)MailItem).LatestMailItem;
        public IMailItem FirstMailItem => ((IMailItemThread)MailItem).FirstMailItem;

        public string Id => ((IMailItem)MailItem).Id;
        public string Subject => ((IMailItem)MailItem).Subject;
        public string ThreadId => ((IMailItem)MailItem).ThreadId;
        public string MessageId => ((IMailItem)MailItem).MessageId;
        public string References => ((IMailItem)MailItem).References;
        public string PreviewText => ((IMailItem)MailItem).PreviewText;
        public string FromName => ((IMailItem)MailItem).FromName;
        public DateTime CreationDate => ((IMailItem)MailItem).CreationDate;
        public string FromAddress => ((IMailItem)MailItem).FromAddress;
        public bool HasAttachments => ((IMailItem)MailItem).HasAttachments;
        public bool IsFlagged => ((IMailItem)MailItem).IsFlagged;
        public bool IsFocused => ((IMailItem)MailItem).IsFocused;
        public bool IsRead => ((IMailItem)MailItem).IsRead;
        public bool IsDraft => ((IMailItem)MailItem).IsDraft;
        public string DraftId => string.Empty;
        public string InReplyTo => ((IMailItem)MailItem).InReplyTo;

        public MailItemFolder AssignedFolder => ((IMailItem)MailItem).AssignedFolder;

        public MailAccount AssignedAccount => ((IMailItem)MailItem).AssignedAccount;

        public Guid UniqueId => ((IMailItem)MailItem).UniqueId;

        public Guid FileId => ((IMailItem)MailItem).FileId;

        public int CompareTo(DateTime other) => CreationDate.CompareTo(other);
        public int CompareTo(string other) => FromName.CompareTo(other);

        // Get single mail item view model out of the only item in thread items.
        public MailItemViewModel GetSingleItemViewModel() => ThreadItems.First() as MailItemViewModel;

        public IEnumerable<Guid> GetContainingIds() => ((IMailItemThread)MailItem).GetContainingIds();
    }
}
