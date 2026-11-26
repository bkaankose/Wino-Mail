using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace Wino.Providers.Exchange.Synchronizers.Exchange.ActiveSync;

/// <summary>
/// What a Change or Delete command does to the stored copy of item <see cref="ServerId"/> once the server accepts it,
/// for a request sent again after its response was lost: <see cref="Deletes"/> removes the item, otherwise the item is
/// stored again from <see cref="Data"/>. A command without an effect takes its own application data.
/// </summary>
internal sealed record EasCommandEffect(string ServerId, bool Deletes, XElement? Data)
{
    internal static string Write(IReadOnlyList<EasCommandEffect>? effects)
        => effects == null || effects.Count == 0 ? string.Empty
            : new XElement("Effects", effects.Select(effect => new XElement("Effect", new XAttribute("ServerId", effect.ServerId),
                new XAttribute("Deletes", effect.Deletes), effect.Data == null ? null : new XElement(effect.Data)))).ToString(SaveOptions.DisableFormatting);

    internal static IReadOnlyList<EasCommandEffect> Read(string text)
    {
        if (string.IsNullOrEmpty(text))
            return [];
        return XElement.Parse(text).Elements("Effect")
            .Select(effect => new EasCommandEffect((string?)effect.Attribute("ServerId") ?? string.Empty, (bool?)effect.Attribute("Deletes") == true,
                effect.Elements().FirstOrDefault() is { } data ? new XElement(data) : null))
            .ToList();
    }
}
