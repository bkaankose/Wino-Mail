using System;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.ViewModels.Links;

/// <summary>The other side of an <see cref="ItemLink"/>, as a screen that shows one item lists it.</summary>
public sealed class LinkedItemViewModel
{
    public LinkedItemViewModel(ItemLink link, ItemLinkKind kind, Guid accountId, string key, string title, string subtitle, bool isAvailable, bool isCompleted)
    {
        Link = link;
        Kind = kind;
        AccountId = accountId;
        Key = key;
        Title = string.IsNullOrWhiteSpace(title) ? Translator.MailItemNoSubject : title;
        Subtitle = subtitle ?? string.Empty;
        IsAvailable = isAvailable;
        IsCompleted = isCompleted;
    }

    public ItemLink Link { get; }
    public ItemLinkKind Kind { get; }
    public Guid AccountId { get; }
    public string Key { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public bool HasSubtitle => Subtitle.Length > 0;
    public bool IsAvailable { get; }
    public bool IsCompleted { get; }

    public WinoIconGlyph Glyph => Kind switch
    {
        ItemLinkKind.Mail => WinoIconGlyph.Mail,
        ItemLinkKind.Event => WinoIconGlyph.Calendar,
        ItemLinkKind.Task => IsCompleted ? WinoIconGlyph.CheckmarkCircleFilled : WinoIconGlyph.TaskList,
        _ => WinoIconGlyph.Link
    };

    public string AutomationName => string.Format(Translator.ConnectedItems_OpenLinkedItemFormat, Title);
}
