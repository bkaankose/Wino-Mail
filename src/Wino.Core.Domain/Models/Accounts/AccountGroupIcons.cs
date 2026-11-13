using System;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Accounts;

public static class AccountGroupIcons
{
    public static AccountGroupIcon[] All { get; } = Enum.GetValues<AccountGroupIcon>();

    /// <summary>The glyph of <paramref name="icon"/>; a value from a newer build falls back to the default icon.</summary>
    public static WinoIconGlyph GetGlyph(AccountGroupIcon icon) => icon switch
    {
        AccountGroupIcon.People => WinoIconGlyph.People,
        AccountGroupIcon.PeopleTeam => WinoIconGlyph.PeopleTeam,
        AccountGroupIcon.Briefcase => WinoIconGlyph.Briefcase,
        AccountGroupIcon.Home => WinoIconGlyph.Home,
        AccountGroupIcon.Heart => WinoIconGlyph.Heart,
        AccountGroupIcon.Globe => WinoIconGlyph.Globe,
        AccountGroupIcon.Star => WinoIconGlyph.Star,
        AccountGroupIcon.Shield => WinoIconGlyph.Shield,
        AccountGroupIcon.Cart => WinoIconGlyph.Cart,
        AccountGroupIcon.Airplane => WinoIconGlyph.Airplane,
        AccountGroupIcon.Tag => WinoIconGlyph.Tag,
        AccountGroupIcon.Folder => WinoIconGlyph.Folder,
        AccountGroupIcon.Mail => WinoIconGlyph.Mail,
        AccountGroupIcon.Lightbulb => WinoIconGlyph.Lightbulb,
        _ => WinoIconGlyph.PeopleLink
    };

    public static AccountGroupIcon Normalize(AccountGroupIcon icon)
        => Enum.IsDefined(icon) ? icon : AccountGroupIcon.PeopleLink;

    public static AccountGroupIcon FromStoredValue(int? value)
        => value is int number ? Normalize((AccountGroupIcon)number) : AccountGroupIcon.PeopleLink;

    public static string GetDisplayName(AccountGroupIcon icon) => Normalize(icon) switch
    {
        AccountGroupIcon.People => Translator.AccountGroupIcon_People,
        AccountGroupIcon.PeopleTeam => Translator.AccountGroupIcon_Team,
        AccountGroupIcon.Briefcase => Translator.AccountGroupIcon_Work,
        AccountGroupIcon.Home => Translator.AccountGroupIcon_Home,
        AccountGroupIcon.Heart => Translator.AccountGroupIcon_Heart,
        AccountGroupIcon.Globe => Translator.AccountGroupIcon_Globe,
        AccountGroupIcon.Star => Translator.AccountGroupIcon_Star,
        AccountGroupIcon.Shield => Translator.AccountGroupIcon_Shield,
        AccountGroupIcon.Cart => Translator.AccountGroupIcon_Shopping,
        AccountGroupIcon.Airplane => Translator.AccountGroupIcon_Travel,
        AccountGroupIcon.Tag => Translator.AccountGroupIcon_Tag,
        AccountGroupIcon.Folder => Translator.AccountGroupIcon_Folder,
        AccountGroupIcon.Mail => Translator.AccountGroupIcon_Mail,
        AccountGroupIcon.Lightbulb => Translator.AccountGroupIcon_Ideas,
        _ => Translator.AccountGroupIcon_LinkedPeople
    };
}
