using System.Collections.Generic;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.CardDav;

namespace Wino.Core.Domain.Interfaces;

public interface IVCardCodec
{
    VCardDocument Parse(string content);
    string Serialize(VCardDocument document);
    string GetUid(VCardDocument document);

    AccountContact Project(VCardDocument document);
    VCardDocument Create(AccountContact contact, string version, string uid = null);
    void Patch(VCardDocument document, AccountContact contact);

    /// <summary>
    /// Writes into the card only what differs between <paramref name="original"/> (the contact the
    /// edit started from) and <paramref name="contact"/>. Everything else on the card is kept as it is.
    /// </summary>
    void Patch(VCardDocument document, AccountContact contact, AccountContact original);
    void SetPhoto(VCardDocument document, byte[] photo);

    /// <summary>The inline photo of the card, or null when it has none or only a photo URL.</summary>
    byte[] GetPhoto(VCardDocument document);
    void RemovePhoto(VCardDocument document);

    /// <summary>True for a group vCard (Apple X-ADDRESSBOOKSERVER-KIND or RFC 6350 KIND).</summary>
    IReadOnlyList<string> GetCategories(VCardDocument document);
    void SetCategories(VCardDocument document, IEnumerable<string> categoryNames);
    VCardDocument CreateGroup(string name, string version, string uid);
    bool IsGroup(VCardDocument document);
    string GetGroupName(VCardDocument document);
    void SetGroupName(VCardDocument document, string name);
    IReadOnlyList<string> GetGroupMembers(VCardDocument document);
    void SetGroupMembers(VCardDocument document, IEnumerable<string> memberUids);
}
