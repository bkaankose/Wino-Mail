using System;

namespace Wino.Core.Domain.Models.Browsers;

/// <summary>
/// A browser, or one profile of it, that can open web links. Ids are platform specific and only
/// meaningful on the device that listed them.
/// </summary>
public sealed record BrowserOption(string BrowserId, string? ProfileId, string DisplayName)
{
    public bool IsSameTarget(BrowserOption other)
        => string.Equals(BrowserId, other.BrowserId, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(ProfileId, other.ProfileId, StringComparison.Ordinal);
}
