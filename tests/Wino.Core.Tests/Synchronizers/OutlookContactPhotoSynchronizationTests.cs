using System.Text;
using FluentAssertions;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Synchronizers.Mail;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

public class OutlookContactPhotoSynchronizationTests
{
    [Fact]
    public void BuildMissingPhotoKey_IsIdempotent()
    {
        var missingKey = OutlookSynchronizer.BuildMissingPhotoKey("change-key");

        OutlookSynchronizer.BuildMissingPhotoKey(missingKey).Should().Be(missingKey);
    }

    [Fact]
    public void BuildHiddenPhotoKey_IsIdempotent()
    {
        var hiddenKey = OutlookSynchronizer.BuildHiddenPhotoKey("change-key");

        OutlookSynchronizer.BuildHiddenPhotoKey(hiddenKey).Should().Be(hiddenKey);
    }

    [Fact]
    public void PreservePhotoSuppression_UsesTheUpdatedRemoteVersion()
    {
        var pictureId = Guid.NewGuid();
        var existing = new AccountContact { RemotePhotoKey = OutlookSynchronizer.BuildHiddenPhotoKey("old-version") };
        var updated = new AccountContact { RemotePhotoKey = "new-version", ContactPictureFileId = pictureId };

        OutlookSynchronizer.PreserveOutlookContactPhotoSuppression(updated, existing);

        updated.RemotePhotoKey.Should().Be(OutlookSynchronizer.BuildHiddenPhotoKey("new-version"));
        updated.ContactPictureFileId.Should().BeNull();
    }

    [Fact]
    public void CreatePhotoSuppression_UsesTheContactCommittedByThePrecedingUpdate()
    {
        var requested = new AccountContact
        {
            Id = Guid.NewGuid(),
            RemotePhotoKey = "pre-update-version",
            ContactPictureFileId = Guid.NewGuid()
        };
        var committed = new AccountContact
        {
            Id = requested.Id,
            RemotePhotoKey = "post-update-version",
            ContactPictureFileId = requested.ContactPictureFileId
        };

        var suppressed = OutlookSynchronizer.CreateOutlookContactPhotoSuppression(requested, committed);

        suppressed.Should().NotBeSameAs(committed);
        suppressed.RemotePhotoKey.Should().Be(OutlookSynchronizer.BuildHiddenPhotoKey("post-update-version"));
        suppressed.ContactPictureFileId.Should().BeNull();
    }

    [Fact]
    public void MissingRemotePhoto_IsSkippedWhileTheContactIsUnchanged()
    {
        var existing = new AccountContact
        {
            RemoteId = "contact-id",
            RemotePhotoKey = OutlookSynchronizer.BuildMissingPhotoKey("change-key")
        };
        var incoming = new AccountContact
        {
            RemoteId = existing.RemoteId,
            RemotePhotoKey = "change-key"
        };

        var reused = OutlookSynchronizer.TryReuseOutlookContactPhoto(incoming, existing);

        reused.Should().BeTrue();
        incoming.RemotePhotoKey.Should().Be(existing.RemotePhotoKey);
        incoming.ContactPictureFileId.Should().BeNull();
    }

    [Fact]
    public void MissingRemotePhoto_IsRefetchedAfterTheContactChanges()
    {
        var existing = new AccountContact { RemoteId = "contact-id", RemotePhotoKey = OutlookSynchronizer.BuildMissingPhotoKey("change-key") };
        var incoming = new AccountContact { RemoteId = existing.RemoteId, RemotePhotoKey = "new-change-key" };

        OutlookSynchronizer.TryReuseOutlookContactPhoto(incoming, existing).Should().BeFalse();
    }

    [Fact]
    public void LocallyHiddenRemotePhoto_RemainsHiddenAfterTheContactChanges()
    {
        var existing = new AccountContact { RemoteId = "contact-id", RemotePhotoKey = OutlookSynchronizer.BuildHiddenPhotoKey("change-key") };
        var incoming = new AccountContact
        {
            RemoteId = existing.RemoteId,
            RemotePhotoKey = "new-change-key",
            ContactPictureFileId = Guid.NewGuid()
        };

        OutlookSynchronizer.TryReuseOutlookContactPhoto(incoming, existing).Should().BeTrue();
        incoming.RemotePhotoKey.Should().Be(OutlookSynchronizer.BuildHiddenPhotoKey("new-change-key"));
        incoming.ContactPictureFileId.Should().BeNull();
    }

    [Fact]
    public void UnchangedRemotePhoto_ReusesCachedPictureFile()
    {
        var pictureId = Guid.NewGuid();
        var existing = new AccountContact
        {
            RemoteId = "contact-id",
            RemotePhotoKey = "change-key",
            ContactPictureFileId = pictureId
        };
        var incoming = new AccountContact
        {
            RemoteId = existing.RemoteId,
            RemotePhotoKey = existing.RemotePhotoKey
        };

        var reused = OutlookSynchronizer.TryReuseOutlookContactPhoto(incoming, existing);

        reused.Should().BeTrue();
        incoming.ContactPictureFileId.Should().Be(pictureId);
    }

    [Fact]
    public void Base64EncodedImageCache_IsRecognizedWithoutInvalidatingRawOrArbitraryFiles()
    {
        byte[] pngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var encodedPng = Encoding.UTF8.GetBytes(Convert.ToBase64String(pngBytes));

        OutlookSynchronizer.IsBase64EncodedImageCache(encodedPng).Should().BeTrue();
        OutlookSynchronizer.IsBase64EncodedImageCache(pngBytes).Should().BeFalse();
        OutlookSynchronizer.IsBase64EncodedImageCache("ordinary text"u8.ToArray()).Should().BeFalse();
    }
}
