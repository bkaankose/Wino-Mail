using System;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;

namespace Wino.Mail.ViewModels.FlaggedMail;

/// <summary>A flagged mail shown as a task in To Do.</summary>
public sealed partial class FlaggedMailItemViewModel : ObservableObject
{
    public FlaggedMailItemViewModel(MailCopy mail)
    {
        Mail = mail ?? throw new ArgumentNullException(nameof(mail));
    }

    public MailCopy Mail { get; }
    public Guid UniqueId => Mail.UniqueId;
    public string Subject => string.IsNullOrWhiteSpace(Mail.Subject) ? Translator.MailItemNoSubject : Mail.Subject;
    public string Sender => string.IsNullOrWhiteSpace(Mail.FromName) ? Mail.FromAddress ?? string.Empty : Mail.FromName;
    public string AccountName => Mail.AssignedAccount?.Name ?? string.Empty;
    public string ReceivedText => Mail.CreationDate == default ? string.Empty : Mail.CreationDate.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public string DetailsText => string.Join(" • ", new[] { Sender, AccountName, ReceivedText }.Where(static value => !string.IsNullOrWhiteSpace(value)));

    /// <summary>The flag is being cleared; the row leaves when the change is applied.</summary>
    [ObservableProperty]
    public partial bool IsCompleting { get; set; }
}
