using System;
using System.Collections.Generic;
using System.Linq;

namespace Wino.Core.Domain.Models.Accounts;

/// <summary>A predefined account group text color: one value for light surfaces and one for dark surfaces.</summary>
public sealed record AccountGroupColor(string Key, string LightHex, string DarkHex)
{
    public string DisplayName => AccountGroupColors.GetDisplayName(Key);
}

public static class AccountGroupColors
{
    public static AccountGroupColor[] All { get; } =
    [
        new("Red", "#B91C1C", "#F87171"),
        new("Orange", "#C2410C", "#FB923C"),
        new("Amber", "#B45309", "#FBBF24"),
        new("Gold", "#946200", "#FACC15"),
        new("Olive", "#626A1C", "#C5CB7A"),
        new("Lime", "#4D7C0F", "#A3E635"),
        new("Green", "#15803D", "#4ADE80"),
        new("Emerald", "#047857", "#34D399"),
        new("Teal", "#0F766E", "#2DD4BF"),
        new("Cyan", "#0E7490", "#22D3EE"),
        new("Sky", "#0369A1", "#38BDF8"),
        new("Blue", "#1D4ED8", "#60A5FA"),
        new("Indigo", "#4338CA", "#818CF8"),
        new("Violet", "#6D28D9", "#A78BFA"),
        new("Purple", "#7E22CE", "#C084FC"),
        new("Magenta", "#A21CAF", "#E879F9"),
        new("Pink", "#BE185D", "#F472B6"),
        new("Rose", "#BE123C", "#FB7185"),
        new("Brown", "#7C4A2D", "#D6A27C"),
        new("Slate", "#475569", "#94A3B8")
    ];

    /// <summary>The palette entry for <paramref name="key"/>, or null for the default text color and for unknown keys.</summary>
    public static AccountGroupColor? Find(string? key)
        => string.IsNullOrWhiteSpace(key)
            ? null
            : All.FirstOrDefault(color => string.Equals(color.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>A stored key normalized to its palette spelling, or null when it is not in the palette.</summary>
    public static string? Normalize(string? key) => Find(key)?.Key;

    public static string GetDisplayName(string? key) => Find(key)?.Key switch
    {
        "Red" => Translator.AccountGroupColor_Red,
        "Orange" => Translator.AccountGroupColor_Orange,
        "Amber" => Translator.AccountGroupColor_Amber,
        "Gold" => Translator.AccountGroupColor_Gold,
        "Olive" => Translator.AccountGroupColor_Olive,
        "Lime" => Translator.AccountGroupColor_Lime,
        "Green" => Translator.AccountGroupColor_Green,
        "Emerald" => Translator.AccountGroupColor_Emerald,
        "Teal" => Translator.AccountGroupColor_Teal,
        "Cyan" => Translator.AccountGroupColor_Cyan,
        "Sky" => Translator.AccountGroupColor_Sky,
        "Blue" => Translator.AccountGroupColor_Blue,
        "Indigo" => Translator.AccountGroupColor_Indigo,
        "Violet" => Translator.AccountGroupColor_Violet,
        "Purple" => Translator.AccountGroupColor_Purple,
        "Magenta" => Translator.AccountGroupColor_Magenta,
        "Pink" => Translator.AccountGroupColor_Pink,
        "Rose" => Translator.AccountGroupColor_Rose,
        "Brown" => Translator.AccountGroupColor_Brown,
        "Slate" => Translator.AccountGroupColor_Slate,
        _ => Translator.AccountGroupColor_Default
    };

    /// <summary>The hex for <paramref name="key"/> on a light or dark surface, or null for the default text color.</summary>
    public static string? GetHex(string? key, bool isDarkTheme)
        => Find(key) is { } color ? (isDarkTheme ? color.DarkHex : color.LightHex) : null;
}
