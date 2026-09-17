using System;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Owns the per-Exchange-account push listeners (MAPI/HTTP notification session or EWS streaming
/// subscription) for the lifetime of the process, so they keep running while the app is minimized.
/// </summary>
public interface IExchangeStreamingNotificationService
{
    /// <summary>Starts a listener for every existing Exchange account.</summary>
    Task StartAsync();

    /// <summary>Stops and disposes all listeners.</summary>
    Task StopAsync();

    /// <summary>Starts a listener for the given account (no-op for non-Exchange accounts).</summary>
    Task StartForAccountAsync(MailAccount account);

    /// <summary>Stops and disposes the listener for the given account, if any.</summary>
    Task StopForAccountAsync(Guid accountId);

    /// <summary>True while the account's push channel is open (so the poll can be relaxed).</summary>
    bool IsStreaming(Guid accountId);

    /// <summary>
    /// How many times the account's open push channel has closed. Server changes made while it was
    /// closed were not pushed, so a relaxed poll reconciles once whenever this value moves.
    /// </summary>
    int GetInterruptionCount(Guid accountId);
}
