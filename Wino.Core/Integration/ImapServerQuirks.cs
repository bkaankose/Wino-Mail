using System;
using System.Collections.Generic;

namespace Wino.Core.Integration;

internal sealed class ImapServerQuirkProfile
{
    public static readonly ImapServerQuirkProfile Default = new();

    public bool DisableQResync { get; init; }
    public bool DisableCondstore { get; init; }
    public bool UseConservativeConnections { get; init; }
}

internal static class ImapServerQuirks
{
    private static readonly Dictionary<string, ImapServerQuirkProfile> Quirks = new(StringComparer.OrdinalIgnoreCase)
    {
        // Some strict providers are more stable with conservative behavior.
        ["qq.com"] = new ImapServerQuirkProfile { DisableQResync = true, UseConservativeConnections = true },
        ["163.com"] = new ImapServerQuirkProfile { DisableQResync = true, UseConservativeConnections = true },
        ["126.com"] = new ImapServerQuirkProfile { DisableQResync = true, UseConservativeConnections = true },
        ["yeah.net"] = new ImapServerQuirkProfile { DisableQResync = true, UseConservativeConnections = true }
    };

    public static ImapServerQuirkProfile Resolve(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return ImapServerQuirkProfile.Default;

        foreach (var (key, profile) in Quirks)
        {
            if (host.Contains(key, StringComparison.OrdinalIgnoreCase))
                return profile;
        }

        return ImapServerQuirkProfile.Default;
    }
}
