using CommunityToolkit.Mvvm.ComponentModel;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Entities.Shared;

namespace MailCal.Calendar.ViewModels.Data;

public partial class CalendarComposeAttendeeViewModel : ObservableObject, IContactDisplayItem
{
    public string DisplayName { get; }
    public string Email { get; }
    public AccountContact ResolvedContact { get; }
    public string Address => Email;
    public AccountContact PreviewContact => ResolvedContact;
    public bool HasDistinctDisplayName => !string.IsNullOrWhiteSpace(DisplayName) && !DisplayName.Equals(Email, System.StringComparison.OrdinalIgnoreCase);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AttendanceTypeText))]
    public partial bool IsOptional { get; set; }

    public string AttendanceTypeText => IsOptional
        ? Translator.CalendarEventCompose_AttendeeOptional
        : Translator.CalendarEventCompose_AttendeeRequired;

    public CalendarComposeAttendeeViewModel(string displayName, string email, AccountContact resolvedContact = null)
    {
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? email : displayName;
        Email = email;
        ResolvedContact = resolvedContact;
    }

    public static CalendarComposeAttendeeViewModel FromContact(AccountContact contact)
        => new(contact.Name, contact.Address, contact);
}
