#nullable enable
using System;
using System.Collections.Generic;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.ViewModels.ActionCards;

/// <summary>The schema.org types Wino draws a designed card for. Every other type gets the <see cref="Other"/> card.</summary>
public enum ActionCardKind
{
    Flight,
    Train,
    Bus,
    Lodging,
    RentalCar,
    Restaurant,
    Event,
    Order,
    Parcel,
    Invoice,
    Other
}

public enum ActionCardActionKind
{
    /// <summary>Opens a web address the sender put in the card. Wino cannot verify it, so the user is asked first.</summary>
    OpenSenderLink,
    AddToCalendar,
    CreateTask
}

/// <summary>A button on an action card.</summary>
public sealed class ActionCardAction
{
    public required ActionCardActionKind Kind { get; init; }
    public required string Label { get; init; }
    public WinoIconGlyph Glyph { get; init; }
    public Uri? Address { get; init; }
    public DateTime? Start { get; init; }
    public DateTime? End { get; init; }
    public bool IsAllDay { get; init; }
    public string ItemTitle { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;

    public bool IsSenderLink => Kind == ActionCardActionKind.OpenSenderLink;
}

/// <summary>A labelled value. A copyable value gets a copy button (booking codes, tracking numbers).</summary>
public sealed class ActionCardField
{
    public ActionCardField(string label, string value, bool isCopyable = false)
    {
        Label = label;
        Value = value;
        IsCopyable = isCopyable;
    }

    public string Label { get; }
    public string Value { get; }
    public bool IsCopyable { get; }
}

/// <summary>A line of an order: what was bought and its price.</summary>
public sealed class ActionCardLine
{
    public ActionCardLine(string text, string amount)
    {
        Text = text;
        Amount = amount;
    }

    public string Text { get; }
    public string Amount { get; }
}

/// <summary>One step of a delivery's progress.</summary>
public sealed class ActionCardStep
{
    public ActionCardStep(string label, bool isReached, bool isCurrent)
    {
        Label = label;
        IsReached = isReached;
        IsCurrent = isCurrent;
    }

    public string Label { get; }
    public bool IsReached { get; }
    public bool IsCurrent { get; }
}

/// <summary>
/// What one card shows. A card is built from the sender's structured data and is read-only; which sections it fills
/// depends on its kind (a flight has a departure and an arrival, an order has lines, a parcel has steps).
/// </summary>
public sealed class ActionCardModel
{
    public required ActionCardKind Kind { get; init; }
    public required WinoIconGlyph Glyph { get; init; }
    public required string Title { get; init; }
    public string Summary { get; init; } = string.Empty;

    /// <summary>A bold first line of the body, such as an event's date and time.</summary>
    public string Headline { get; init; } = string.Empty;

    /// <summary>A line under the headline, such as a venue or an address.</summary>
    public string Subheadline { get; init; } = string.Empty;

    public string DepartureTime { get; init; } = string.Empty;
    public string DeparturePlace { get; init; } = string.Empty;
    public string ArrivalTime { get; init; } = string.Empty;
    public string ArrivalPlace { get; init; } = string.Empty;

    public string AmountLabel { get; init; } = string.Empty;
    public string Amount { get; init; } = string.Empty;

    public List<ActionCardField> Fields { get; init; } = [];
    public List<ActionCardLine> Lines { get; init; } = [];
    public string Total { get; init; } = string.Empty;
    public List<ActionCardStep> Steps { get; init; } = [];

    /// <summary>Whether the card opens expanded; the reader sets it from the state the user last left a card in.</summary>
    public bool IsExpanded { get; set; }

    public ActionCardAction? PrimaryAction { get; init; }
    public List<ActionCardAction> MoreActions { get; init; } = [];

    public bool HasHeadline => Headline.Length > 0;
    public bool HasSubheadline => Subheadline.Length > 0;
    public bool HasLeg => DepartureTime.Length > 0 && ArrivalTime.Length > 0;
    public bool HasAmount => Amount.Length > 0;
    public bool HasFields => Fields.Count > 0;
    public bool HasLines => Lines.Count > 0;
    public bool HasTotal => Total.Length > 0;
    public bool HasSteps => Steps.Count > 0;
    public bool HasPrimaryAction => PrimaryAction is not null;
    public bool HasMoreActions => MoreActions.Count > 0;
}
