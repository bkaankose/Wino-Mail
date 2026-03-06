using Wino.Core.Domain.Entities.Shared;

namespace Wino.Calendar.ViewModels.Data;

public class CalendarComposeAttendeeViewModel
{
    public string DisplayName { get; }
    public string Email { get; }
    public AccountContact ResolvedContact { get; }
    public bool HasDistinctDisplayName => !string.IsNullOrWhiteSpace(DisplayName) && !DisplayName.Equals(Email, System.StringComparison.OrdinalIgnoreCase);

    public CalendarComposeAttendeeViewModel(string displayName, string email, AccountContact resolvedContact = null)
    {
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? email : displayName;
        Email = email;
        ResolvedContact = resolvedContact;
    }

    public static CalendarComposeAttendeeViewModel FromContact(AccountContact contact)
        => new(contact.Name, contact.Address, contact);
}
