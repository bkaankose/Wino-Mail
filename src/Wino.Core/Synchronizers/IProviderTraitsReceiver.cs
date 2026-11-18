using Wino.Core.Domain.Models.Accounts;

namespace Wino.Core.Synchronizers;

/// <summary>
/// Hands a synchronizer the traits of the module that created it. <see cref="Services.SynchronizerFactory"/> sets them
/// right after the module builds the synchronizer, so provider constructors do not carry them.
/// </summary>
internal interface IProviderTraitsReceiver
{
    MailProviderTraits ProviderTraits { set; }
}
