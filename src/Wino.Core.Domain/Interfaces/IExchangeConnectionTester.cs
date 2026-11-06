#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Checks an Exchange account's server and credential before setup saves it or account details keep a new password,
/// and learns which transport the mailbox offers.
/// </summary>
public interface IExchangeConnectionTester
{
    /// <summary>Only cancellation throws; every other failure is in the result.</summary>
    Task<ExchangeConnectionTestResult> TestAsync(MailAccount account, CancellationToken cancellationToken = default);
}
