using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Browsers;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Platform adapter that lists installed browsers and opens a web link in one of them.
/// Implementations never throw; failures return an incomplete catalog or false.
/// </summary>
public interface IInstalledBrowserService
{
    bool IsSupported { get; }

    Task<BrowserCatalog> GetBrowsersAsync(CancellationToken cancellationToken = default);

    Task<bool> TryLaunchAsync(BrowserOption browser, Uri uri, CancellationToken cancellationToken = default);
}
