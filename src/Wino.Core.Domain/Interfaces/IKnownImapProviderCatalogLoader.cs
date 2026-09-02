using System.IO;
using Wino.Core.Domain.Models.Connectivity;

namespace Wino.Core.Domain.Interfaces;

public interface IKnownImapProviderCatalogLoader
{
    KnownImapProviderCatalogDocument Load(Stream source);
}
