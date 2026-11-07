#nullable enable
using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Wino.Mail.ViewModels.Data;

/// <summary>A learned fact on "What Wino remembers": the user's words, what they point at, and a delete button.</summary>
public sealed partial class VoiceBrainFactItem : ObservableObject
{
    private readonly Func<VoiceBrainFactItem, Task> _delete;

    public VoiceBrainFactItem(string id, string title, string? detail, string deleteAutomationName, Func<VoiceBrainFactItem, Task> delete)
    {
        Id = id;
        Title = title;
        Detail = detail;
        DeleteAutomationName = deleteAutomationName;
        _delete = delete;
    }

    public string Id { get; }

    public string Title { get; }

    public string? Detail { get; }

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    public string DeleteAutomationName { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    public partial bool IsDeleting { get; set; }

    private bool CanDelete() => !IsDeleting;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private Task DeleteAsync() => _delete(this);
}

/// <summary>A mailbox the server keeps a section for, with when it was last updated.</summary>
public sealed record VoiceBrainSectionItem(string Title, string Detail);
