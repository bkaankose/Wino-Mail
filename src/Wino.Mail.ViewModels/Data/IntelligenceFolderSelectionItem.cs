#nullable enable
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;

namespace Wino.Mail.ViewModels.Data;

public partial class IntelligenceFolderSelectionItem(
    string remoteFolderId,
    string displayName,
    bool isSelected,
    int availableMessageCount = 0,
    int indentLevel = 0) : ObservableObject
{
    /// <summary>Left inset per level of folder nesting, in effective pixels.</summary>
    private const double IndentSize = 20;

    public string RemoteFolderId { get; } = remoteFolderId;
    public string DisplayName { get; } = displayName;

    /// <summary>
    /// How much mail including this folder would add. Shown in the picker because that is where
    /// the decision is made, and a folder name alone does not say whether it costs 80 messages or 8,000.
    /// </summary>
    public int AvailableMessageCount { get; } = availableMessageCount;

    /// <summary>Depth under the account root, used to indent the picker rather than print a path.</summary>
    public int IndentLevel { get; } = indentLevel;

    public double IndentMargin => IndentLevel * IndentSize;

    public string AvailableMessageCountText
        => string.Format(Translator.SemanticIndex_CoverageMessageCount, AvailableMessageCount);

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = isSelected;
}
