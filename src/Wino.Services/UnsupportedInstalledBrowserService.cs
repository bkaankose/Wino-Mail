using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Browsers;

namespace Wino.Services;

/// <summary>Used until a platform provides its own browser list; every link then opens with the system default.</summary>
public sealed class UnsupportedInstalledBrowserService : IInstalledBrowserService
{
    public bool IsSupported => false;

    public Task<BrowserCatalog> GetBrowsersAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(BrowserCatalog.Unavailable);

    public Task<bool> TryLaunchAsync(BrowserOption browser, Uri uri, CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}
