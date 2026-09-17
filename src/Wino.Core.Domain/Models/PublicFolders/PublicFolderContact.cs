namespace Wino.Core.Domain.Models.PublicFolders;

/// <summary>
/// A transient contact read live from a public contacts folder (class IPF.Contact). Never persisted into the
/// email-keyed address book: public folder contacts are shown only under their source folder, so they cannot
/// collide with the user's own contacts on the email address key.
/// </summary>
public sealed class PublicFolderContact
{
    public string RemoteId { get; init; }
    public string DisplayName { get; init; }
    public string Address { get; init; }
    public string Company { get; init; }
    public string Title { get; init; }
    public string BusinessPhone { get; init; }
    public string HomePhone { get; init; }
    public string MobilePhone { get; init; }
    public string BusinessFax { get; init; }
    public string StreetAddress { get; init; }
    public string Notes { get; init; }
}
