using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Mail.ViewModels.Data
{
    /// <summary>
    /// Thread mail item (multiple IMailItem) view model representation.
    /// </summary>
    public class ThreadMailItemViewModel : ObservableObject, IMailItemThread, IComparable<string>, IComparable<DateTime>
    {
        public ObservableCollection<IMailItem> ThreadItems => ((IMailItemThread)_threadMailItem).ThreadItems;

        private readonly ThreadMailItem _threadMailItem;

        private bool isThreadExpanded;
        public bool IsThreadExpanded
        {
            get => isThreadExpanded;
            set => SetProperty(ref isThreadExpanded, value);
        }

        public ThreadMailItemViewModel(ThreadMailItem threadMailItem)
        {
            _threadMailItem = new ThreadMailItem();

            // Local copies
            foreach (var item in threadMailItem.ThreadItems)
            {
                AddMailItemViewModel(item);
            }
        }

        public IEnumerable<MailCopy> GetMailCopies()
            => ThreadItems.OfType<MailItemViewModel>().Select(a => a.MailCopy);

        public void AddMailItemViewModel(IMailItem mailItem)
        {
            if (mailItem == null) return;

            if (mailItem is MailCopy mailCopy)
                _threadMailItem.AddThreadItem(new MailItemViewModel(mailCopy));
            else if (mailItem is MailItemViewModel mailItemViewModel)
                _threadMailItem.AddThreadItem(mailItemViewModel);
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
            OnPropertyChanged(nameof(Subject));
            OnPropertyChanged(nameof(PreviewText));
            OnPropertyChanged(nameof(FromName));
            OnPropertyChanged(nameof(FromAddress));
            OnPropertyChanged(nameof(HasAttachments));

            OnPropertyChanged(nameof(IsFlagged));
            OnPropertyChanged(nameof(IsDraft));
            OnPropertyChanged(nameof(IsRead));
            OnPropertyChanged(nameof(IsFocused));
            OnPropertyChanged(nameof(CreationDate));
        }

        public IMailItem LatestMailItem => ((IMailItemThread)_threadMailItem).LatestMailItem;
        public IMailItem FirstMailItem => ((IMailItemThread)_threadMailItem).FirstMailItem;

        public string Id => ((IMailItem)_threadMailItem).Id;
        public string Subject => ((IMailItem)_threadMailItem).Subject;
        public string ThreadId => ((IMailItem)_threadMailItem).ThreadId;
        public string MessageId => ((IMailItem)_threadMailItem).MessageId;
        public string References => ((IMailItem)_threadMailItem).References;
        public string PreviewText => ((IMailItem)_threadMailItem).PreviewText;
        public string FromName => ((IMailItem)_threadMailItem).FromName;
        public DateTime CreationDate => ((IMailItem)_threadMailItem).CreationDate;
        public string FromAddress => ((IMailItem)_threadMailItem).FromAddress;
        public bool HasAttachments => ((IMailItem)_threadMailItem).HasAttachments;
        public bool IsFlagged => ((IMailItem)_threadMailItem).IsFlagged;
        public bool IsFocused => ((IMailItem)_threadMailItem).IsFocused;
        public bool IsRead => ((IMailItem)_threadMailItem).IsRead;
        public bool IsDraft => ((IMailItem)_threadMailItem).IsDraft;
        public string DraftId => string.Empty;
        public string InReplyTo => ((IMailItem)_threadMailItem).InReplyTo;

        public MailItemFolder AssignedFolder => ((IMailItem)_threadMailItem).AssignedFolder;

        public MailAccount AssignedAccount => ((IMailItem)_threadMailItem).AssignedAccount;

        public Guid UniqueId => ((IMailItem)_threadMailItem).UniqueId;

        public Guid FileId => ((IMailItem)_threadMailItem).FileId;

        public int CompareTo(DateTime other) => CreationDate.CompareTo(other);
        public int CompareTo(string other) => FromName.CompareTo(other);

        // Get single mail item view model out of the only item in thread items.
        public MailItemViewModel GetSingleItemViewModel() => ThreadItems.First() as MailItemViewModel;
    }
}
