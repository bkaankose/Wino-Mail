#nullable enable
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Accounts;

/// <summary>
/// Which app modes the footer switcher offers. <see cref="AvailableModes"/> are the modes at
/// least one account provides; <see cref="VisibleModes"/> removes the ones the user hid.
/// Settings is never part of either list because it is always reachable.
/// </summary>
public sealed record AppModeVisibility(
    IReadOnlyList<WinoApplicationMode> AvailableModes,
    IReadOnlyList<WinoApplicationMode> VisibleModes)
{
    public static readonly WinoApplicationMode[] SwitchableModes =
    [
        WinoApplicationMode.Mail,
        WinoApplicationMode.Calendar,
        WinoApplicationMode.Contacts,
        WinoApplicationMode.Tasks
    ];

    public static readonly AppModeVisibility MailOnly = new(new[] { WinoApplicationMode.Mail }, new[] { WinoApplicationMode.Mail });

    public static readonly AppModeVisibility All = new(SwitchableModes, SwitchableModes);

    public WinoApplicationMode FallbackMode => VisibleModes[0];

    public bool IsAvailable(WinoApplicationMode mode)
        => mode == WinoApplicationMode.Settings || AvailableModes.Contains(mode);

    public bool IsVisible(WinoApplicationMode mode)
        => mode == WinoApplicationMode.Settings || VisibleModes.Contains(mode);

    /// <summary>
    /// A mode can be hidden while another one stays visible; the last one always shows.
    /// </summary>
    public bool CanHide(WinoApplicationMode mode)
        => IsVisible(mode) && mode != WinoApplicationMode.Settings && VisibleModes.Count > 1;

    public static AppModeVisibility Evaluate(
        IReadOnlyCollection<MailAccount>? accounts,
        IReadOnlyCollection<WinoApplicationMode>? hiddenModes)
    {
        if (accounts == null || accounts.Count == 0)
            return MailOnly;

        var available = SwitchableModes
            .Where(mode => accounts.Any(account => AppModeReadiness.IsModeEnabled(account, mode)))
            .ToList();

        if (available.Count == 0)
            return MailOnly;

        var visible = hiddenModes == null
            ? available
            : available.Where(mode => !hiddenModes.Contains(mode)).ToList();

        if (visible.Count == 0)
            visible = [available[0]];

        return new AppModeVisibility(available, visible);
    }

    public bool Equals(AppModeVisibility? other)
        => other != null &&
           AvailableModes.SequenceEqual(other.AvailableModes) &&
           VisibleModes.SequenceEqual(other.VisibleModes);

    public override int GetHashCode()
    {
        var hash = new System.HashCode();

        foreach (var mode in AvailableModes)
            hash.Add(mode);

        foreach (var mode in VisibleModes)
            hash.Add(mode);

        return hash.ToHashCode();
    }
}
