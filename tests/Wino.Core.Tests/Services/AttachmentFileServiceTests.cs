using System.IO;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Attachments;
using Wino.Core.Domain.Models.Platform;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class AttachmentFileServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"wino-attachment-tests-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("..\\..\\payload.exe", "payload.exe")]
    [InlineData("report.pdf:payload.exe", "report.pdf_payload.exe")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("   ", "attachment.bin")]
    public void SanitizeFileName_ProducesSafeLeafName(string input, string expected)
    {
        AttachmentFileService.SanitizeFileName(input).Should().Be(expected);
    }

    [Fact]
    public async Task SaveAsync_ReceivedAttachmentAppliesWindowsPolicy()
    {
        var policy = new Mock<IAttachmentPlatformService>();
        var service = CreateService(policy);
        var source = CreateSource(AttachmentFileOrigin.Received);

        var result = await service.SaveAsync(source, _root);

        result.IsSuccess.Should().BeTrue();
        File.ReadAllText(result.FilePath!).Should().Be("attachment content");
        policy.Verify(item => item.ApplySavePolicyAsync(result.FilePath!, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SaveAsync_LocalAttachmentDoesNotApplyWindowsPolicy()
    {
        Directory.CreateDirectory(_root);
        var localPath = Path.Combine(_root, "source.txt");
        await File.WriteAllTextAsync(localPath, "attachment content");
        var policy = new Mock<IAttachmentPlatformService>();
        var service = CreateService(policy);
        var source = CreateSource(AttachmentFileOrigin.Local, localPath);
        var destination = Path.Combine(_root, "destination");

        var result = await service.SaveAsync(source, destination);

        result.IsSuccess.Should().BeTrue();
        policy.Verify(item => item.ApplySavePolicyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OpenAsync_ReceivedAttachmentMapsPolicyFailure()
    {
        var policy = new Mock<IAttachmentPlatformService>();
        policy.Setup(item => item.OpenReceivedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AttachmentFileOperationResult(AttachmentFileOperationStatus.PolicyBlocked));
        var service = CreateService(policy);
        var source = CreateSource(AttachmentFileOrigin.Received);

        var result = await service.OpenAsync(
            source,
            _root,
            ContentTypeDetectionResult.Unavailable,
            mismatchApproved: false);

        result.Status.Should().Be(AttachmentFileOperationStatus.PolicyBlocked);
        policy.Verify(item => item.OpenReceivedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OpenAsync_ReceivedAttachmentMapsPolicyCancellation()
    {
        var policy = new Mock<IAttachmentPlatformService>();
        policy.Setup(item => item.OpenReceivedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AttachmentFileOperationResult(AttachmentFileOperationStatus.Cancelled));
        var service = CreateService(policy);
        var source = CreateSource(AttachmentFileOrigin.Received);

        var result = await service.OpenAsync(
            source,
            _root,
            ContentTypeDetectionResult.Unavailable,
            mismatchApproved: false);

        result.Status.Should().Be(AttachmentFileOperationStatus.Cancelled);
    }

    [Fact]
    public async Task InspectAsync_StreamFailureReturnsFailed()
    {
        var policy = new Mock<IAttachmentPlatformService>();
        var service = CreateService(policy);
        var source = new AttachmentFileSource(
            "missing.txt",
            "text/plain",
            AttachmentFileOrigin.Received,
            _ => throw new FileNotFoundException());

        var result = await service.InspectAsync(source);

        result.Status.Should().Be(ContentTypeDetectionStatus.Failed);
    }

    [Theory]
    [InlineData(PlatformOperationStatus.Failed, AttachmentFileOperationStatus.Failed)]
    [InlineData(PlatformOperationStatus.Unavailable, AttachmentFileOperationStatus.Unavailable)]
    [InlineData(PlatformOperationStatus.Cancelled, AttachmentFileOperationStatus.Cancelled)]
    public async Task OpenAsync_LocalAttachmentDoesNotReportDeclinedLaunchAsSuccess(
        PlatformOperationStatus launchStatus, AttachmentFileOperationStatus expectedStatus)
    {
        Directory.CreateDirectory(_root);
        var localPath = Path.Combine(_root, "source.txt");
        await File.WriteAllTextAsync(localPath, "attachment content");
        var launcher = new Mock<IExternalLauncher>();
        launcher.Setup(x => x.LaunchFileAsync(localPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformOperationResult(launchStatus));
        var service = new AttachmentFileService(Mock.Of<IContentTypeDetectionService>(), launcher.Object,
            Mock.Of<IAttachmentPlatformService>(), Mock.Of<IWinoLogger>());

        var result = await service.OpenAsync(CreateSource(AttachmentFileOrigin.Local, localPath), _root,
            ContentTypeDetectionResult.Unavailable, mismatchApproved: false);

        result.Status.Should().Be(expectedStatus);
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task OpenAsync_ReceivedAttachmentRemainsAvailableWhenPlatformIsUnavailable()
    {
        var platform = new Mock<IAttachmentPlatformService>();
        platform.Setup(x => x.OpenReceivedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AttachmentFileOperationResult(AttachmentFileOperationStatus.Unavailable));

        var result = await CreateService(platform).OpenAsync(CreateSource(AttachmentFileOrigin.Received), _root,
            ContentTypeDetectionResult.Unavailable, mismatchApproved: false);

        result.Status.Should().Be(AttachmentFileOperationStatus.Unavailable);
        File.ReadAllText(result.FilePath!).Should().Be("attachment content");
    }

    [Fact]
    public async Task OpenAsync_FailedMaterializationCleansOnlyItsOperationFolder()
    {
        Directory.CreateDirectory(_root);
        var unrelated = Path.Combine(_root, "keep.txt");
        await File.WriteAllTextAsync(unrelated, "keep");
        var source = new AttachmentFileSource("broken.txt", "text/plain", AttachmentFileOrigin.Received,
            _ => throw new IOException("Cannot read attachment."));

        var result = await CreateService(new Mock<IAttachmentPlatformService>()).OpenAsync(source, _root,
            ContentTypeDetectionResult.Unavailable, mismatchApproved: false);

        result.Status.Should().Be(AttachmentFileOperationStatus.Failed);
        Directory.GetDirectories(_root).Should().BeEmpty();
        File.ReadAllText(unrelated).Should().Be("keep");
    }

    [Fact]
    public async Task OpenAsync_MismatchDoesNotMaterializeOrLaunchBeforeUserConfirms()
    {
        var platform = new Mock<IAttachmentPlatformService>(MockBehavior.Strict);
        var detection = new ContentTypeDetectionResult(ContentTypeDetectionStatus.Detected,
            IsExtensionMismatch: true);

        var result = await CreateService(platform).OpenAsync(CreateSource(AttachmentFileOrigin.Received), _root,
            detection, mismatchApproved: false);

        result.Status.Should().Be(AttachmentFileOperationStatus.ConfirmationRequired);
        Directory.Exists(_root).Should().BeFalse();
        platform.Verify(x => x.OpenReceivedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static AttachmentFileService CreateService(Mock<IAttachmentPlatformService> policy)
    {
        policy.Setup(item => item.ApplySavePolicyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AttachmentFileOperationResult(AttachmentFileOperationStatus.Succeeded));

        var detector = new Mock<IContentTypeDetectionService>();
        detector.Setup(item => item.DetectAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ContentTypeDetectionResult.Unavailable);

        return new AttachmentFileService(
            detector.Object,
            Mock.Of<IExternalLauncher>(),
            policy.Object,
            Mock.Of<IWinoLogger>());
    }

    private static AttachmentFileSource CreateSource(AttachmentFileOrigin origin, string? localPath = null) =>
        new(
            "attachment.txt",
            "text/plain",
            origin,
            _ => ValueTask.FromResult<Stream>(new MemoryStream("attachment content"u8.ToArray())),
            localPath);
}
