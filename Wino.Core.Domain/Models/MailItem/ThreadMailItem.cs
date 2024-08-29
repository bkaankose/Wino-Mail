using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Wino.Core.Domain.Entities;

namespace Wino.Core.Domain.Models.MailItem
{
    public class ThreadMailItem : IMailItemThread
    {
        // TODO: Ideally this should be SortedList.
        public ObservableCollection<IMailItem> ThreadItems { get; } = new ObservableCollection<IMailItem>();

        public IMailItem LatestMailItem => ThreadItems.LastOrDefault();
        public IMailItem FirstMailItem => ThreadItems.FirstOrDefault();

        public bool AddThreadItem(IMailItem item)
        {
            if (item == null) return false;

            if (ThreadItems.Any(a => a.Id == item.Id))
            {
                return false;
            }

            if (item != null && item.IsDraft)
            {
                ThreadItems.Insert(0, item);
                return true;
            }

            var insertItem = ThreadItems.FirstOrDefault(a => !a.IsDraft && a.CreationDate < item.CreationDate);

            if (insertItem == null)
                ThreadItems.Insert(ThreadItems.Count, item);
            else
            {
                var index = ThreadItems.IndexOf(insertItem);

                ThreadItems.Insert(index, item);
            }

            return true;
        }

        public IEnumerable<Guid> GetContainingIds() => ThreadItems?.Select(a => a.UniqueId) ?? default;

        #region IMailItem

        public Guid UniqueId => LatestMailItem?.UniqueId ?? Guid.Empty;
        public string Id => LatestMailItem?.Id ?? string.Empty;

        // Show subject from last item.
        public string Subject => LatestMailItem?.Subject ?? string.Empty;

        public string ThreadId => LatestMailItem?.ThreadId ?? string.Empty;

        public string PreviewText => FirstMailItem?.PreviewText ?? string.Empty;

        public string FromName => LatestMailItem?.FromName ?? string.Empty;

        public string FromAddress => LatestMailItem?.FromAddress ?? string.Empty;

        public bool HasAttachments => ThreadItems.Any(a => a.HasAttachments);

        public bool IsFlagged => ThreadItems.Any(a => a.IsFlagged);

        public bool IsFocused => LatestMailItem?.IsFocused ?? false;

        public bool IsRead => ThreadItems.All(a => a.IsRead);

        public DateTime CreationDate => FirstMailItem?.CreationDate ?? DateTime.MinValue;

        public bool IsDraft => ThreadItems.Any(a => a.IsDraft);

        public string DraftId => string.Empty;

        public string MessageId => LatestMailItem?.MessageId;

        public string References => LatestMailItem?.References ?? string.Empty;

        public string InReplyTo => LatestMailItem?.InReplyTo ?? string.Empty;

        public MailItemFolder AssignedFolder => LatestMailItem?.AssignedFolder;

        public MailAccount AssignedAccount => LatestMailItem?.AssignedAccount;

        public Guid FileId => LatestMailItem?.FileId ?? Guid.Empty;

        public AccountContact SenderContact => LatestMailItem?.SenderContact;

        #endregion
    }
}
