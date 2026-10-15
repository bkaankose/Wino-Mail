#nullable enable
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain;
using Wino.Mail.Controls.Core.ToDo;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// What the native To Do surfaces show for one task (the Windows task card and the detail drawer header), shared by the
/// Mac, iOS and Android pages: the card row content and the drawer's status line.
/// </summary>
public static class TaskRowPresentation
{
    /// <summary>The task card content; <paramref name="listColorHex"/> is the owning list's colour (#RRGGBB) or null.</summary>
    public static WinoTaskRowModel Describe(TaskItemViewModel item, string? listColorHex) => new()
    {
        Title = item.Title ?? string.Empty,
        IsCompleted = item.IsCompleted,
        IsImportant = item.IsImportant,
        IsEditable = item.IsEditable,
        ListName = item.ShowListName && !string.IsNullOrEmpty(item.ListName) ? item.ListName : null,
        ListColorHex = listColorHex,
        DueText = item.HasDueDate ? item.DueDisplayText : string.Empty,
        IsOverdue = item.IsOverdue,
        StepSummary = item.HasSteps ? item.StepSummaryText : string.Empty,
        IsInMyDay = item.IsInMyDay,
        HasNote = item.HasNote,
        MyDayText = Translator.ToDoPage_MyDay,
        ImportanceActionText = item.ImportanceActionText,
        CompletionActionText = item.CompletionActionText
    };

    /// <summary>The detail drawer's status line under the title: due text, My Day and the list name (Windows drawer header).</summary>
    public static string DetailSummary(TaskItemViewModel task)
    {
        var parts = new List<string>(3);
        if (task.HasDueDate) parts.Add(task.DueDisplayText);
        if (task.IsInMyDay) parts.Add(Translator.ToDoPage_MyDay);
        if (task.ShowSummaryListName && !string.IsNullOrEmpty(task.ListName)) parts.Add(task.ListName);
        return string.Join("  •  ", parts.Where(part => !string.IsNullOrEmpty(part)));
    }
}
