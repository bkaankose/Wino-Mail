using System.IO;
using MailCal.Core.Domain.Models.Connectivity;

namespace MailCal.Core.Domain.Interfaces;

public interface IKnownImapProviderCatalogLoader
{
    KnownImapProviderCatalogDocument Load(Stream source);
}
