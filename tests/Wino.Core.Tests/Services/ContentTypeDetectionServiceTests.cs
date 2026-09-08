using System.IO;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.ML;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class ContentTypeDetectionServiceTests
{
    [Fact]
    public async Task DetectAsync_FlagsHighConfidenceExtensionMismatch()
    {
        var model = new Mock<IContentTypeClassificationModel>();
        model.SetupGet(item => item.IsSupported).Returns(true);
        model.Setup(item => item.ClassifyAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContentTypeClassificationResult(
                ContentTypeClassificationStatus.Detected,
                "pebin",
                "pebin",
                "application/x-dosexec",
                "PE Windows executable",
                "executable",
                0.99,
                ["exe", "dll"]));
        var service = new ContentTypeDetectionService(model.Object, Mock.Of<IWinoLogger>());
        await using var content = new MemoryStream(new byte[32]);

        var result = await service.DetectAsync(content, "invoice.pdf", "application/pdf");

        result.Status.Should().Be(ContentTypeDetectionStatus.Detected);
        result.IsExtensionMismatch.Should().BeTrue();
        result.RequiresOpenConfirmation.Should().BeTrue();
        result.IsDeclaredMimeMismatch.Should().BeTrue();
    }

    [Fact]
    public async Task DetectAsync_DoesNotWarnForLowConfidenceOrGenericMime()
    {
        var model = new Mock<IContentTypeClassificationModel>();
        model.SetupGet(item => item.IsSupported).Returns(true);
        model.Setup(item => item.ClassifyAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContentTypeClassificationResult(
                ContentTypeClassificationStatus.LowConfidence,
                "unknown",
                "pdf",
                "application/octet-stream",
                "Unknown binary data",
                "unknown",
                0.2,
                ["bin"]));
        var service = new ContentTypeDetectionService(model.Object, Mock.Of<IWinoLogger>());
        await using var content = new MemoryStream(new byte[32]);

        var result = await service.DetectAsync(content, "invoice.pdf", "application/octet-stream");

        result.Status.Should().Be(ContentTypeDetectionStatus.LowConfidence);
        result.IsExtensionMismatch.Should().BeFalse();
        result.IsDeclaredMimeMismatch.Should().BeFalse();
    }

    [Fact]
    public async Task DetectAsync_ReturnsUnavailableWithoutInvokingModel()
    {
        var model = new Mock<IContentTypeClassificationModel>();
        model.SetupGet(item => item.IsSupported).Returns(false);
        var service = new ContentTypeDetectionService(model.Object, Mock.Of<IWinoLogger>());
        await using var content = new MemoryStream(new byte[32]);

        var result = await service.DetectAsync(content, "invoice.pdf", "application/pdf");

        result.Status.Should().Be(ContentTypeDetectionStatus.Unavailable);
        model.Verify(item => item.ClassifyAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
