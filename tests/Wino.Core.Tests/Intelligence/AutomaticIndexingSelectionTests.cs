using System;
using System.Collections.Generic;
using FluentAssertions;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Intelligence;
using Xunit;

namespace Wino.Core.Tests.Intelligence;

/// <summary>
/// Which synchronized mails the "New mail: Automatic" coverage setting submits.
/// </summary>
public sealed class AutomaticIndexingSelectionTests
{
    private static readonly Guid AccountId = Guid.Parse("0f0e0d0c-0b0a-0908-0706-050403020100");

    [Fact]
    public void Capture_SkipsDraftsExcludedFoldersAndMailsWithoutIdentity()
    {
        AutomaticIndexingSelection.TryCapture(Mail("draft", SpecialFolderType.Inbox, "inbox", isDraft: true)).Should().BeNull();
        AutomaticIndexingSelection.TryCapture(Mail("junk", SpecialFolderType.Junk, "junk")).Should().BeNull();
        AutomaticIndexingSelection.TryCapture(Mail("deleted", SpecialFolderType.Deleted, "trash")).Should().BeNull();
        AutomaticIndexingSelection.TryCapture(Mail("", SpecialFolderType.Inbox, "inbox")).Should().BeNull();
        AutomaticIndexingSelection.TryCapture(new MailCopy { Id = "orphan" }).Should().BeNull();

        var capture = AutomaticIndexingSelection.TryCapture(Mail("m1", SpecialFolderType.Inbox, "inbox"));
        capture.Should().NotBeNull();
        capture!.RemoteFolderId.Should().Be("inbox");
        capture.SpecialFolderType.Should().Be(SpecialFolderType.Inbox);
    }

    [Fact]
    public void Select_WithoutConfiguredFolders_TakesOnlyInbox()
    {
        var preferences = new MailAccountPreferences { IsIntelligenceFolderSelectionInitialized = false };
        var captured = new List<SynchronizedMailCapture>
        {
            new("m1", "inbox", SpecialFolderType.Inbox),
            new("m2", "archive", SpecialFolderType.Archive),
            new("m3", "projects", SpecialFolderType.Other),
        };

        AutomaticIndexingSelection.Select(preferences, captured).Should().Equal("m1");
    }

    [Fact]
    public void Select_WithConfiguredFolders_FollowsTheSelection()
    {
        var preferences = new MailAccountPreferences
        {
            IsIntelligenceFolderSelectionInitialized = true,
            SelectedIntelligenceFolderIds = new HashSet<string>(StringComparer.Ordinal) { "projects" },
        };
        var captured = new List<SynchronizedMailCapture>
        {
            new("m1", "inbox", SpecialFolderType.Inbox),
            new("m3", "projects", SpecialFolderType.Other),
        };

        AutomaticIndexingSelection.Select(preferences, captured).Should().Equal("m3");
    }

    [Fact]
    public void Select_IncludesAMessageOnceWhenAnyCopySitsInAnIncludedFolder()
    {
        var preferences = new MailAccountPreferences
        {
            IsIntelligenceFolderSelectionInitialized = true,
            SelectedIntelligenceFolderIds = new HashSet<string>(StringComparer.Ordinal) { "work" },
        };
        var captured = new List<SynchronizedMailCapture>
        {
            new("gmail-1", "inbox", SpecialFolderType.Inbox),
            new("gmail-1", "work", SpecialFolderType.Other),
            new("gmail-1", "work", SpecialFolderType.Other),
        };

        AutomaticIndexingSelection.Select(preferences, captured).Should().Equal("gmail-1");
    }

    [Fact]
    public void Select_WithAnEmptySelection_TakesNothing()
    {
        var preferences = new MailAccountPreferences
        {
            IsIntelligenceFolderSelectionInitialized = true,
            SelectedIntelligenceFolderIds = new HashSet<string>(StringComparer.Ordinal),
        };

        AutomaticIndexingSelection.Select(preferences, [new("m1", "inbox", SpecialFolderType.Inbox)]).Should().BeEmpty();
    }

    private static MailCopy Mail(string id, SpecialFolderType folderType, string remoteFolderId, bool isDraft = false) => new()
    {
        Id = id,
        IsDraft = isDraft,
        AssignedAccount = new MailAccount { Id = AccountId, ProviderType = MailProviderType.Outlook },
        AssignedFolder = new MailItemFolder { RemoteFolderId = remoteFolderId, SpecialFolderType = folderType },
    };
}
