using System;
using System.Globalization;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public sealed class WinoPendingCheckoutStore(IConfigurationService configuration) : IWinoPendingCheckoutStore
{
    private const string Key = "WinoPendingCheckout";

    public void Save(Guid accountId, WinoAddOnProductType product)
        => configuration.Set(Key, $"{accountId:D}|{(int)product}|{DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds()}");

    public WinoAddOnProductType? Get(Guid accountId)
    {
        var parts = configuration.Get(Key, string.Empty).Split('|');
        if (parts.Length != 3 || !Guid.TryParse(parts[0], out var owner) || owner != accountId ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var product) ||
            !Enum.IsDefined(typeof(WinoAddOnProductType), product) ||
            !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var expires) ||
            expires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            return null;

        return (WinoAddOnProductType)product;
    }

    public void Clear(Guid accountId)
    {
        if (configuration.Get(Key, string.Empty).StartsWith($"{accountId:D}|", StringComparison.Ordinal))
            configuration.Remove(Key);
    }
}
