#nullable enable
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.SemanticIndexing;

namespace Wino.Mail.ViewModels.Data;

public partial class IntelligenceFolderCoverageItem(
    string remoteFolderId,
    string displayName,
    SemanticIndexFolderCoverageRule rule) : ObservableObject
{
    public string RemoteFolderId { get; } = remoteFolderId;
    public string DisplayName { get; } = displayName;

    [ObservableProperty]
    public partial SemanticIndexFolderCoverageRule Rule { get; set; } = rule;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailableSummary))]
    public partial int AvailableMessageCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedCountSummary))]
    [NotifyPropertyChangedFor(nameof(RowAutomationName))]
    public partial int SelectedMessageCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CoveredSummary))]
    [NotifyPropertyChangedFor(nameof(RowAutomationName))]
    public partial int CoveredMessageCount { get; set; }

    public string AvailableSummary
        => string.Format(Translator.SemanticIndex_CoverageAvailableMessages, AvailableMessageCount);

    public string CoveredSummary
        => string.Format(Translator.SemanticIndex_CoverageCoveredMessages, CoveredMessageCount);

    public string SelectedCountSummary
        => string.Format(Translator.SemanticIndex_CoverageSelectedMessages, SelectedMessageCount);

    public string RowAutomationName => string.Format(
        Translator.SemanticIndex_CoverageFolderRowName,
        DisplayName,
        CoveredMessageCount,
        SelectedMessageCount);
}
