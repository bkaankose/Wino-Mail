using System.Collections.Generic;
using System.Linq;

namespace Wino.Core.Domain.Models.Browsers;

/// <summary>
/// <see cref="IsComplete"/> is false when the platform could not read its browser list. A selection
/// missing from an incomplete catalog is unknown, not stale, and must never be reset.
/// </summary>
public sealed record BrowserCatalog(bool IsComplete, List<BrowserOption> Browsers)
{
    public static BrowserCatalog Unavailable { get; } = new(false, []);

    public bool Contains(BrowserOption selection) => Browsers.Any(browser => browser.IsSameTarget(selection));
}
