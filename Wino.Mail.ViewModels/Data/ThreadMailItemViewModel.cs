using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// Thread mail item (multiple IMailItem) view model representation.
/// </summary>
public partial class ThreadMailItemViewModel : ObservableRecipient, IDisposable, IMailListItem
{
    private readonly string _threadId;

    private bool _disposed;

    [ObservableProperty]
    [NotifyPropertyChangedRecipients]
    [NotifyPropertyChangedFor(nameof(IsSelectedOrExpanded))]
    public partial bool IsThreadExpanded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedRecipients]
    [NotifyPropertyChangedFor(nameof(IsSelectedOrExpanded))]
    public partial bool IsSelected { get; set; }

    public bool IsSelectedOrExpanded => IsSelected || IsThreadExpanded;

    /// <summary>
    /// Gets the number of emails in this thread
    /// </summary>
    public int EmailCount => ThreadEmails.Count;

    /// <summary>
    /// Gets the latest email's subject for display
    /// </summary>
    public string Subject => ThreadEmails
        .OrderByDescending(e => e.MailCopy?.CreationDate)
        .FirstOrDefault()?.MailCopy?.Subject;

    /// <summary>
    /// Gets the latest email's sender name for display
    /// </summary>
    public string FromName => ThreadEmails
        .OrderByDescending(e => e.MailCopy?.CreationDate)
        .FirstOrDefault()?.MailCopy?.SenderContact.Name;

    /// <summary>
    /// Gets the latest email's creation date for sorting
    /// </summary>
    public DateTime CreationDate => ThreadEmails
        .OrderByDescending(e => e.MailCopy?.CreationDate)
        .FirstOrDefault()?.MailCopy?.CreationDate ?? DateTime.MinValue;

    /// <summary>
    /// Gets all emails in this thread (observable)
    /// </summary>
    ///
    [ObservableProperty]
    public partial ObservableCollection<MailItemViewModel> ThreadEmails { get; set; } = [];

    public MailItemViewModel LatestMailViewModel => ThreadEmails.OrderByDescending(e => e.MailCopy?.CreationDate).FirstOrDefault()!;

    public ThreadMailItemViewModel(string threadId)
    {
        _threadId = threadId;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            ThreadEmails.Clear();
        }

        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public void NotifyPropertyChanges()
    {
        OnPropertyChanged(nameof(Subject));
        OnPropertyChanged(nameof(FromName));
        OnPropertyChanged(nameof(CreationDate));
        OnPropertyChanged(nameof(LatestMailViewModel));
        OnPropertyChanged(nameof(ThreadEmails));
    }


    /// <summary>
    /// Adds an email to this thread
    /// </summary>
    public void AddEmail(MailItemViewModel email)
    {
        if (email.MailCopy.ThreadId != _threadId)
            throw new ArgumentException($"Email ThreadId '{email.MailCopy.ThreadId}' does not match expander ThreadId '{_threadId}'");

        ThreadEmails.Add(email);
        NotifyPropertyChanges();
    }

    /// <summary>
    /// Removes an email from this thread
    /// </summary>
    public void RemoveEmail(MailItemViewModel email)
    {
        if (ThreadEmails.Remove(email))
        {
            NotifyPropertyChanges();
        }
    }

    /// <summary>
    /// Checks if this thread contains an email with the specified unique ID
    /// </summary>
    public bool HasUniqueId(Guid uniqueId) => ThreadEmails.Any(email => email.MailCopy.UniqueId == uniqueId);

    public IEnumerable<Guid> GetContainingIds() => ThreadEmails.Select(a => a.MailCopy.UniqueId);

    public IEnumerable<MailItemViewModel> GetSelectedMailItems()
    {
        if (IsSelected)
        {
            // If the thread itself is selected, return all emails in the thread
            return ThreadEmails;
        }
        else
        {
            // Otherwise, return only individually selected emails within the thread
            return ThreadEmails.Where(e => e.IsSelected);
        }
    }
}
