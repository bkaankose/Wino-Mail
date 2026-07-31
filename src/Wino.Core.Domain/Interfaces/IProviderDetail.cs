using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces;

public interface IProviderDetail
{
    MailProviderType Type { get; }
    SpecialImapProvider SpecialImapProvider { get; }
    string Name { get; }
    string Description { get; }
    string ProviderImage { get; }
    bool IsSupported { get; }
}
