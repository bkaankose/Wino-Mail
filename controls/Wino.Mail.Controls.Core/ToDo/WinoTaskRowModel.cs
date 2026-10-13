namespace Wino.Mail.Controls.Core.ToDo;

/// <summary>
/// What one native task card shows. The app maps its TaskItemViewModel to this record so the control
/// libraries stay free of the ViewModel assemblies; colours stay as hex so every platform can parse them.
/// </summary>
public sealed record WinoTaskRowModel
{
    public string Title { get; init; } = string.Empty;
    public bool IsCompleted { get; init; }
    public bool IsImportant { get; init; }
    public bool IsEditable { get; init; } = true;

    /// <summary>Owning list, shown on smart views; null hides the segment.</summary>
    public string? ListName { get; init; }

    /// <summary>The owning list's colour as #RRGGBB; null uses the secondary text colour.</summary>
    public string? ListColorHex { get; init; }

    /// <summary>Relative due text ("Due today", "3 days overdue"); empty hides the segment.</summary>
    public string DueText { get; init; } = string.Empty;
    public bool IsOverdue { get; init; }

    /// <summary>"2 of 5"; empty hides the segment.</summary>
    public string StepSummary { get; init; } = string.Empty;
    public bool IsInMyDay { get; init; }
    public bool HasNote { get; init; }

    /// <summary>Localised strings the card needs for its My Day segment and tooltips.</summary>
    public string MyDayText { get; init; } = "My Day";
    public string ImportanceActionText { get; init; } = string.Empty;
    public string CompletionActionText { get; init; } = string.Empty;
}
