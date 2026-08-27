using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.CardDav;

namespace Wino.Core.Domain.Interfaces;

public interface IVCardCodec
{
    VCardDocument Parse(string content);
    AccountContact Project(VCardDocument document);
    VCardDocument Create(AccountContact contact, string version, string uid = null);
    void Patch(VCardDocument document, AccountContact contact);
    string Serialize(VCardDocument document);
    VCardHashes ComputeHashes(VCardDocument document, AccountContact projection, string rawContent = null);
}
