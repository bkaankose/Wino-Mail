using FluentAssertions;
using Wino.NotificationHost.Contracts;
using Xunit;

namespace Wino.NotificationHost.Tests;

public sealed class NotificationHostFileStoreTests : IDisposable
{
    private readonly string _tempFolder = Path.Combine(Path.GetTempPath(), $"WinoNotificationHost_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempFolder))
            Directory.Delete(_tempFolder, recursive: true);
    }

    [Fact]
    public async Task WriteRequestAsync_PublishesOnlyFinalEnvelope()
    {
        var requestId = Guid.NewGuid();
        var request = new NotificationHostRequest(
            DateTimeOffset.UtcNow,
            NotificationHostOperation.RemoveAll,
            NotificationHostApplication.People,
            null,
            null,
            null);

        await NotificationHostFileStore.WriteRequestAsync(_tempFolder, requestId, request);

        Directory.EnumerateFiles(NotificationHostPaths.GetRequestDirectory(_tempFolder))
            .Should().ContainSingle()
            .Which.Should().Be(NotificationHostPaths.GetRequestPath(_tempFolder, requestId));
        NotificationHostFileStore.ReadRequest(_tempFolder, requestId).Should().BeEquivalentTo(request);
    }

    [Fact]
    public async Task CleanupStaleFiles_RemovesOldRequestsAndActivationsOnly()
    {
        var oldRequestId = Guid.NewGuid();
        var currentActivationId = Guid.NewGuid();
        await NotificationHostFileStore.WriteRequestAsync(
            _tempFolder,
            oldRequestId,
            new NotificationHostRequest(DateTimeOffset.UtcNow, NotificationHostOperation.RemoveAll, NotificationHostApplication.Mail, null, null, null));
        await NotificationHostFileStore.WriteActivationAsync(
            _tempFolder,
            currentActivationId,
            new NotificationHostActivation(DateTimeOffset.UtcNow, NotificationHostApplication.Mail, "ToastModeKey=ToastModeMail", new Dictionary<string, string>()));

        File.SetLastWriteTimeUtc(
            NotificationHostPaths.GetRequestPath(_tempFolder, oldRequestId),
            DateTime.UtcNow.AddDays(-2));

        NotificationHostFileStore.CleanupStaleFiles(_tempFolder, TimeSpan.FromHours(24)).Should().Be(1);
        File.Exists(NotificationHostPaths.GetRequestPath(_tempFolder, oldRequestId)).Should().BeFalse();
        File.Exists(NotificationHostPaths.GetActivationPath(_tempFolder, currentActivationId)).Should().BeTrue();
    }
}
