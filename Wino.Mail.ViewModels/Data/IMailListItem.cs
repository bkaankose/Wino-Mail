using System;
using System.Collections.Generic;
using System.ComponentModel;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// Common interface for mail items that can be displayed in a mail list.
/// Implemented by both MailItemViewModel and ThreadMailItemViewModel.
/// </summary>
public interface IMailListItem : IMailHashContainer, IMailListItemSorting, INotifyPropertyChanged
{
    /// <summary>
    /// Gets the latest creation date for sorting purposes.
    /// For MailItemViewModel: the mail's creation date
    /// For ThreadMailItemViewModel: the latest email's creation date
    /// </summary>
    DateTime CreationDate { get; }

    /// <summary>
    /// Gets the sender's name for grouping purposes.
    /// For MailItemViewModel: the mail's from name
    /// For ThreadMailItemViewModel: the latest email's from name
    /// </summary>
    string FromName { get; }

    /// <summary>
    /// Gets whether this item is selected.
    /// For MailItemViewModel: returns IsSelected
    /// For ThreadMailItemViewModel: returns IsSelected
    /// </summary>
    bool IsSelected { get; set; }

    /// <summary>
    /// Gets all selected mail items within this list item.
    /// For MailItemViewModel: returns itself if IsSelected is true, otherwise empty
    /// For ThreadMailItemViewModel: returns all selected emails within the thread
    /// </summary>
    IEnumerable<MailItemViewModel> GetSelectedMailItems();
}
