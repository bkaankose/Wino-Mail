using System.IO;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Attachments;
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
        var policy = new Mock<IWindowsAttachmentPolicyService>();
        var service = CreateService(policy);
        var source = CreateSource(AttachmentFileOrigin.Received);

        var result = await service.SaveAsync(source, _root);

        result.IsSuccess.Should().BeTrue();
        File.ReadAllText(result.FilePath!).Should().Be("attachment content");
        policy.Verify(item => item.ApplySavePolicy(result.FilePath!), Times.Once);
    }

    [Fact]
    public async Task SaveAsync_LocalAttachmentDoesNotApplyWindowsPolicy()
    {
        Directory.CreateDirectory(_root);
        var localPath = Path.Combine(_root, "source.txt");
        await File.WriteAllTextAsync(localPath, "attachment content");
        var policy = new Mock<IWindowsAttachmentPolicyService>();
        var service = CreateService(policy);
        var source = CreateSource(AttachmentFileOrigin.Local, localPath);
        var destination = Path.Combine(_root, "destination");

        var result = await service.SaveAsync(source, destination);

        result.IsSuccess.Should().BeTrue();
        policy.Verify(item => item.ApplySavePolicy(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task OpenAsync_ReceivedAttachmentMapsPolicyFailure()
    {
        var policy = new Mock<IWindowsAttachmentPolicyService>();
        policy.Setup(item => item.Execute(It.IsAny<string>(), It.IsAny<IntPtr>()))
            .Throws(new WindowsAttachmentPolicyException(unchecked((int)0x80070005)));
        var service = CreateService(policy);
        var source = CreateSource(AttachmentFileOrigin.Received);

        var result = await service.OpenAsync(
            source,
            _root,
            ContentTypeDetectionResult.Unavailable,
            mismatchApproved: false);

        result.Status.Should().Be(AttachmentFileOperationStatus.PolicyBlocked);
        policy.Verify(item => item.Execute(It.IsAny<string>(), It.IsAny<IntPtr>()), Times.Once);
    }

    [Fact]
    public async Task OpenAsync_ReceivedAttachmentMapsPolicyCancellation()
    {
        var policy = new Mock<IWindowsAttachmentPolicyService>();
        policy.Setup(item => item.Execute(It.IsAny<string>(), It.IsAny<IntPtr>()))
            .Throws(new WindowsAttachmentPolicyException(unchecked((int)0x800704C7)));
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
        var policy = new Mock<IWindowsAttachmentPolicyService>();
        var service = CreateService(policy);
        var source = new AttachmentFileSource(
            "missing.txt",
            "text/plain",
            AttachmentFileOrigin.Received,
            _ => throw new FileNotFoundException());

        var result = await service.InspectAsync(source);

        result.Status.Should().Be(ContentTypeDetectionStatus.Failed);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static AttachmentFileService CreateService(Mock<IWindowsAttachmentPolicyService> policy)
    {
        var detector = new Mock<IContentTypeDetectionService>();
        detector.Setup(item => item.DetectAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ContentTypeDetectionResult.Unavailable);

        return new AttachmentFileService(
            detector.Object,
            Mock.Of<INativeAppService>(),
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
