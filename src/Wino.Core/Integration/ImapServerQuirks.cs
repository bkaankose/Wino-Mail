using System;
using System.Collections.Generic;

namespace Wino.Core.Integration;

internal sealed class ImapServerQuirkProfile
{
    public static readonly ImapServerQuirkProfile Default = new();

    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromMinutes(4);

    public bool DisableQResync { get; init; }
    public bool DisableCondstore { get; init; }
    public bool UseConservativeConnections { get; init; }
}

internal static class ImapServerQuirks
{
    private static readonly Dictionary<string, (bool IncludeSubdomains, ImapServerQuirkProfile Profile)> Quirks = new(StringComparer.OrdinalIgnoreCase)
    {
        // 2026-09-28: this endpoint reset silent sessions after 61 seconds. NOOP every
        // 30 seconds preserved them. This measurement is host-specific. See tools/imap-probe.
        ["imap.126.com"] = (false, new ImapServerQuirkProfile
        {
            UseConservativeConnections = true,
            KeepAliveInterval = TimeSpan.FromSeconds(30)
        }),
        // Some strict providers are more stable with conservative behavior.
        ["qq.com"] = (true, new ImapServerQuirkProfile { DisableQResync = true, UseConservativeConnections = true }),
        ["163.com"] = (true, new ImapServerQuirkProfile { DisableQResync = true, UseConservativeConnections = true }),
        // 126 did not advertise QRESYNC in the live check; let capabilities govern its use.
        ["126.com"] = (true, new ImapServerQuirkProfile { UseConservativeConnections = true }),
        ["yeah.net"] = (true, new ImapServerQuirkProfile { DisableQResync = true, UseConservativeConnections = true })
    };

    public static ImapServerQuirkProfile Resolve(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return ImapServerQuirkProfile.Default;

        host = host.TrimEnd('.');

        // Exact entries take precedence over domain-wide entries, regardless of order.
        if (Quirks.TryGetValue(host, out var exact))
            return exact.Profile;

        foreach (var (domain, entry) in Quirks)
        {
            if (entry.IncludeSubdomains && host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))
                return entry.Profile;
        }

        return ImapServerQuirkProfile.Default;
    }
}
