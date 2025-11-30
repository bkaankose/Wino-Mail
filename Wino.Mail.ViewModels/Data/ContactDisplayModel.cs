using Wino.Core.Domain.Entities.Shared;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// Display model for showing a contact with a specific email address in compose/rendering UI.
/// Wraps a Contact entity and exposes the specific email address being used.
/// </summary>
public class ContactDisplayModel
{
    private readonly Contact _contact;
    private readonly string _emailAddress;

    public ContactDisplayModel(Contact contact, string emailAddress)
    {
        _contact = contact;
        _emailAddress = emailAddress;
    }

    /// <summary>
    /// The email address being used for this display context.
    /// </summary>
    public string Address => _emailAddress;

    /// <summary>
    /// Display name - uses contact's DisplayName if available, otherwise falls back to email address.
    /// </summary>
    public string Name => !string.IsNullOrWhiteSpace(_contact?.DisplayName) ? _contact.DisplayName : _emailAddress;

    /// <summary>
    /// Base64-encoded contact photo.
    /// </summary>
    public string Base64ContactPicture => _contact?.Base64ContactPicture;

    /// <summary>
    /// Whether this is a root contact (cannot be deleted).
    /// </summary>
    public bool IsRootContact => _contact?.IsRootContact ?? false;

    /// <summary>
    /// The underlying Contact entity.
    /// </summary>
    public Contact Contact => _contact;
}
