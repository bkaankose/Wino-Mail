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
    public void PreservePhotoSuppression_UsesTheUpdatedRemoteVersion()
    {
        var existing = new AccountContact { RemotePhotoKey = OutlookSynchronizer.BuildMissingPhotoKey("old-version") };
        var updated = new AccountContact { RemotePhotoKey = "new-version" };

        OutlookSynchronizer.PreserveOutlookContactPhotoSuppression(updated, existing);

        updated.RemotePhotoKey.Should().Be(OutlookSynchronizer.BuildMissingPhotoKey("new-version"));
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
}
