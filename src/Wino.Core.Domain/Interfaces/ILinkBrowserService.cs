using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Browsers;
using Wino.Core.Domain.Models.Platform;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Opens web links from mail and calendar content in the browser chosen for the owning account.
/// Anything that is not http or https, and every failure, goes to <see cref="IExternalLauncher"/>.
/// </summary>
public interface ILinkBrowserService
{
    bool IsBrowserSelectionSupported { get; }

    /// <summary>Null means System default.</summary>
    BrowserOption? AllAccountsBrowser { get; set; }

    BrowserOption? GetCustomBrowser(MailAccountPreferences preferences);

    /// <summary>Applies the "equal to All accounts means no override" rule. A null browser means System default.</summary>
    void SetCustomBrowser(MailAccountPreferences preferences, BrowserOption? browser);

    /// <summary>Lists the browsers and resets every selection that is no longer installed, warning once.</summary>
    Task<BrowserCatalog> GetBrowsersAsync(CancellationToken cancellationToken = default);

    Task<PlatformOperationResult> OpenLinkAsync(Uri uri, Guid? accountId, CancellationToken cancellationToken = default);
}
