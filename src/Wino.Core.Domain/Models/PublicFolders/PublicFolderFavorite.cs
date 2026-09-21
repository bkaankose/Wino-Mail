using System;
using System.Text.Json.Serialization;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.PublicFolders;

/// <summary>
/// A public folder the user pinned for quick access. This is the only persisted public folder state; the
/// folder hierarchy and content remain fetched live. Stored as a small JSON list in the configuration store.
/// </summary>
public sealed class PublicFolderFavorite
{
    // Overlay colours handed to pinned public calendars, picked by folder id so a calendar keeps its colour.
    private static readonly string[] CalendarColors = ["#0F8A8A", "#8A5CF6", "#E0708A", "#D98E04", "#2E8B57", "#4169E1"];

    public Guid AccountId { get; set; }

    /// <summary>The provider's id of the public folder.</summary>
    public string FolderId { get; set; }

    public PublicFolderKind Kind { get; set; }

    public string Name { get; set; }

    /// <summary>Overlay colour for a calendar favourite (unused for the other kinds).</summary>
    public string ColorHex { get; set; }

    /// <summary>Whether a calendar favourite is ticked in the Calendar pane (unused for the other kinds).</summary>
    public bool IsChecked { get; set; } = true;

    /// <summary>The name the favourite is listed under: the folder name marked as public.</summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? Translator.PublicFolders_PinnedSuffix
        : Name + " " + Translator.PublicFolders_PinnedSuffix;

    /// <summary>
    /// A favourite for a pinnable public folder. The kind decides where it surfaces (folders, People or
    /// Calendar); a calendar also gets its overlay colour here, stable for the folder.
    /// </summary>
    public static PublicFolderFavorite Create(Guid accountId, string folderId, PublicFolderKind kind, string name)
        => new()
        {
            AccountId = accountId,
            FolderId = folderId,
            Kind = kind,
            Name = name,
            ColorHex = kind == PublicFolderKind.Calendar ? PickCalendarColor(folderId) : null
        };

    private static string PickCalendarColor(string folderId)
    {
        // string.GetHashCode is randomised per process; this one is not.
        uint hash = 2166136261;

        foreach (var character in folderId ?? string.Empty)
        {
            hash = (hash ^ character) * 16777619;
        }

        return CalendarColors[hash % (uint)CalendarColors.Length];
    }
}
