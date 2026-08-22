#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.SemanticIndexing;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// One folder in the coverage editor's tree, carrying its own coverage rule.
/// </summary>
/// <remarks>
/// Replaces the flat picker row this page used to show. The old list indented rows by depth and
/// then sorted them alphabetically, so a child never sat under its parent and the indentation
/// read as noise; a real tree makes the parent relationship the thing you navigate by.
/// <para>
/// There is no separate inclusion switch. A folder is included exactly when its rule selects
/// something, so giving a folder coverage includes it and clearing its coverage removes it —
/// one control instead of a checkbox that could disagree with the range beside it.
/// </para>
/// </remarks>
public partial class IntelligenceFolderNode : ObservableObject
{
    public IntelligenceFolderNode(
        string remoteFolderId,
        string displayName,
        int availableMessageCount,
        SemanticIndexFolderCoverageRule rule)
    {
        RemoteFolderId = remoteFolderId;
        DisplayName = displayName;
        AvailableMessageCount = availableMessageCount;
        Rule = rule;
    }

    public string RemoteFolderId { get; }

    public string DisplayName { get; }

    /// <summary>
    /// How much mail including this folder would add. Shown next to the name because a folder name
    /// alone does not say whether it costs 80 messages or 8,000.
    /// </summary>
    public int AvailableMessageCount { get; }

    public ObservableCollection<IntelligenceFolderNode> ChildNodes { get; } = [];

    public IntelligenceFolderNode? Parent { get; private set; }

    public bool HasChildren => ChildNodes.Count > 0;

    /// <summary>How many of this folder's messages its rule currently selects.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIncluded))]
    [NotifyPropertyChangedFor(nameof(CoverageBadgeTooltip))]
    [NotifyPropertyChangedFor(nameof(RowAutomationName))]
    public partial int SelectedMessageCount { get; set; }

    /// <summary>This folder's own rule.</summary>
    [ObservableProperty]
    public partial SemanticIndexFolderCoverageRule Rule { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    /// <summary>Whether this folder's mail is indexed, which is the same thing as it having coverage.</summary>
    public bool IsIncluded => SelectedMessageCount > 0;

    public bool HasMessages => AvailableMessageCount > 0;

    public string AvailableMessageCountText
        => string.Format(Translator.SemanticIndex_CoverageMessageCount, AvailableMessageCount);

    public string CoverageBadgeTooltip
        => string.Format(Translator.SemanticIndex_CoverageFolderIncludedBadge, SelectedMessageCount);

    public string RowAutomationName => string.Format(
        Translator.SemanticIndex_CoverageFolderNodeName,
        DisplayName,
        AvailableMessageCount,
        IsIncluded
            ? string.Format(Translator.SemanticIndex_CoverageFolderIncludedBadge, SelectedMessageCount)
            : Translator.SemanticIndex_CoverageFolderExcluded);

    public void AddChild(IntelligenceFolderNode child)
    {
        child.Parent = this;
        ChildNodes.Add(child);
        OnPropertyChanged(nameof(HasChildren));
    }

    /// <summary>Every node at or below this one, parents before children.</summary>
    public IEnumerable<IntelligenceFolderNode> SelfAndDescendants()
    {
        yield return this;
        foreach (var child in ChildNodes)
        {
            foreach (var node in child.SelfAndDescendants())
                yield return node;
        }
    }

    /// <summary>
    /// Builds the selectable folder forest for one account.
    /// </summary>
    /// <remarks>
    /// Excluded folders are dropped but their children are kept, re-parented onto the nearest
    /// selectable ancestor. A user folder nested under Junk is still a user folder, and hiding it
    /// because of where it sits would make it unreachable.
    /// </remarks>
    /// <param name="includedRemoteFolderIds">
    /// Folders already being indexed. Everything else starts on a rule that selects nothing, which
    /// is what keeps it out of the selection until the user gives it a range.
    /// </param>
    public static IReadOnlyList<IntelligenceFolderNode> Build(
        IReadOnlyCollection<MailItemFolder> folders,
        IntelligenceCoverageInventory inventory,
        IReadOnlySet<string> includedRemoteFolderIds,
        IReadOnlyDictionary<string, SemanticIndexFolderCoverageRule> rulesByFolderId,
        SemanticIndexFolderCoverageRule defaultRule)
    {
        var foldersByRemoteId = folders
            .Where(static folder => !string.IsNullOrWhiteSpace(folder.RemoteFolderId))
            .GroupBy(static folder => folder.RemoteFolderId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);

        var selectable = foldersByRemoteId.Values.Where(IntelligenceFolderFilter.IsSelectable).ToArray();
        var displayNames = GetDisplayNames(selectable, foldersByRemoteId);
        var nodes = selectable.ToDictionary(
            static folder => folder.RemoteFolderId,
            folder => new IntelligenceFolderNode(
                folder.RemoteFolderId,
                displayNames[folder.RemoteFolderId],
                inventory.GetFolderIndices(folder.RemoteFolderId).Length,
                ResolveInitialRule(folder.RemoteFolderId, includedRemoteFolderIds, rulesByFolderId, defaultRule)),
            StringComparer.Ordinal);

        var roots = new List<IntelligenceFolderNode>();
        foreach (var folder in selectable.OrderBy(folder => displayNames[folder.RemoteFolderId], StringComparer.CurrentCultureIgnoreCase))
        {
            var node = nodes[folder.RemoteFolderId];
            var parent = FindSelectableAncestor(folder, foldersByRemoteId, nodes);

            // Two folders can name each other as parent. Attaching both would leave the forest with
            // no roots at all, which loses every folder in the cycle rather than just misplacing it.
            if (parent is null || WouldCycle(node, parent))
                roots.Add(node);
            else
                parent.AddChild(node);
        }

        return roots;
    }

    private static SemanticIndexFolderCoverageRule ResolveInitialRule(
        string remoteFolderId,
        IReadOnlySet<string> includedRemoteFolderIds,
        IReadOnlyDictionary<string, SemanticIndexFolderCoverageRule> rulesByFolderId,
        SemanticIndexFolderCoverageRule defaultRule)
    {
        if (!includedRemoteFolderIds.Contains(remoteFolderId))
            return SemanticIndexFolderCoverageRule.Latest(remoteFolderId, 0);

        return rulesByFolderId.TryGetValue(remoteFolderId, out var stored)
            ? stored
            : defaultRule with { RemoteFolderId = remoteFolderId };
    }

    /// <summary>Whether attaching <paramref name="node"/> under <paramref name="parent"/> closes a loop.</summary>
    private static bool WouldCycle(IntelligenceFolderNode node, IntelligenceFolderNode parent)
    {
        for (var ancestor = parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ReferenceEquals(ancestor, node))
                return true;
        }
        return false;
    }

    private static IntelligenceFolderNode? FindSelectableAncestor(
        MailItemFolder folder,
        IReadOnlyDictionary<string, MailItemFolder> foldersByRemoteId,
        IReadOnlyDictionary<string, IntelligenceFolderNode> nodes)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal) { folder.RemoteFolderId };
        var parentId = folder.ParentRemoteFolderId;

        // Cycles in the parent chain are treated as roots rather than looping forever, matching
        // how the old depth walk handled them.
        while (!string.IsNullOrWhiteSpace(parentId) &&
               foldersByRemoteId.TryGetValue(parentId, out var parent) &&
               visited.Add(parentId))
        {
            if (nodes.TryGetValue(parentId, out var node))
                return node;
            parentId = parent.ParentRemoteFolderId;
        }
        return null;
    }

    /// <summary>
    /// Folder names, disambiguated to a full path only when two folders share a name. The tree
    /// already shows nesting, so a path is redundant noise everywhere else.
    /// </summary>
    private static IReadOnlyDictionary<string, string> GetDisplayNames(
        IReadOnlyCollection<MailItemFolder> selectable,
        IReadOnlyDictionary<string, MailItemFolder> foldersByRemoteId)
    {
        var duplicateNames = selectable
            .GroupBy(static folder => folder.FolderName, StringComparer.CurrentCultureIgnoreCase)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);

        return selectable.ToDictionary(
            static folder => folder.RemoteFolderId,
            folder => duplicateNames.Contains(folder.FolderName)
                ? GetDisplayPath(folder, foldersByRemoteId)
                : folder.FolderName,
            StringComparer.Ordinal);
    }

    private static string GetDisplayPath(
        MailItemFolder folder, IReadOnlyDictionary<string, MailItemFolder> foldersByRemoteId)
    {
        var path = new Stack<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        MailItemFolder? current = folder;

        while (current is not null && visited.Add(current.RemoteFolderId))
        {
            path.Push(current.FolderName);
            current = !string.IsNullOrWhiteSpace(current.ParentRemoteFolderId) &&
                foldersByRemoteId.TryGetValue(current.ParentRemoteFolderId, out var parent)
                ? parent
                : null;
        }

        return string.Join(" / ", path);
    }
}
