#nullable enable
using System;
using System.Collections.Generic;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Models.Tasks;

/// <summary>A new task prefilled from a mail or an event, shown for review before it is created.</summary>
public sealed class TaskFromItemDraft
{
    public string Title { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
    public DateTime? DueDate { get; init; }

    /// <summary>Writable lists the task can go to.</summary>
    public IReadOnlyList<AccountTaskList> Lists { get; init; } = [];
    public IReadOnlyList<MailAccount> Accounts { get; init; } = [];
    public Guid? SelectedListId { get; init; }
}

public sealed record TaskFromItemResult(string Title, string Notes, AccountTaskList List, DateTime? DueDate);
