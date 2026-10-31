#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Decides how an address is set up: consumer domain, catalog, MX host, Microsoft user realm (only behind a known mail
/// filter), then IMAP autodiscovery. Reports a progress line per network stage, marks the stage slow after
/// <see cref="AccountRouteDeadlines.Soft"/>, and ends as <see cref="AccountRoute.NotFound"/> after <see cref="AccountRouteDeadlines.Hard"/>.
/// Throws only when <paramref name="cancellationToken"/> is cancelled.
/// </summary>
public interface IAccountRouteResolver
{
    Task<AccountRouteResolution> ResolveAsync(
        AccountRouteRequest request,
        IProgress<AccountRouteProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
