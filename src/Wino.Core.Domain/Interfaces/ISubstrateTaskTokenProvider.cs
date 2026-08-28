using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Supplies a token for the Exchange Online resource that fronts the undocumented To Do
/// substrate API. It is a separate resource from Graph, so MSAL cannot fold it into the
/// account's normal token request and it has to be acquired on its own.
///
/// The substrate call is an enrichment pass, never a dependency: an account that has not
/// consented to the resource simply gets no token, and task synchronization carries on
/// through Graph as before.
/// </summary>
public interface ISubstrateTaskTokenProvider
{
    Task<string> GetSubstrateTaskTokenAsync(MailAccount account);
    Task EnsureSubstrateTaskConsentAsync(MailAccount account);
}
