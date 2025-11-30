using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels.Extensions;

public static class ContactExtensions
{
    /// <summary>
    /// Converts a Contact entity to ContactDisplayModel with explicit email address.
    /// Use this when you know the email address from context (e.g., from MimeMessage).
    /// </summary>
    public static ContactDisplayModel ToContactDisplayModel(this Contact contact, string emailAddress)
    {
        if (contact == null)
            return null;

        return new ContactDisplayModel(contact, emailAddress);
    }
}
