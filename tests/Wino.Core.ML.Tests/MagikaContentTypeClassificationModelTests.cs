using System.Runtime.InteropServices;
using FluentAssertions;
using Xunit;

namespace Wino.Core.ML.Tests;

public sealed class MagikaContentTypeClassificationModelTests
{
    [Fact]
    public async Task ClassifyAsync_ReportsPlatformAvailability()
    {
        using var model = CreateModel();
        await using var stream = new MemoryStream("hello world"u8.ToArray());

        var result = await model.ClassifyAsync(stream);

        if (RuntimeInformation.ProcessArchitecture == Architecture.X86)
        {
            model.IsSupported.Should().BeFalse();
            result.Status.Should().Be(ContentTypeClassificationStatus.Unavailable);
        }
        else
        {
            model.IsSupported.Should().BeTrue();
            result.Status.Should().Be(ContentTypeClassificationStatus.Detected);
            result.Label.Should().Be("txt");
        }
    }

    [Fact]
    public async Task ClassifyAsync_RecognizesPngAndRestoresStreamPosition()
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.X86)
            return;

        using var model = new MagikaContentTypeClassificationModel();
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        await using var stream = new MemoryStream(png);
        stream.Position = 3;

        var result = await model.ClassifyAsync(stream);

        result.Status.Should().Be(ContentTypeClassificationStatus.Detected);
        result.Label.Should().Be("png");
        result.CompatibleExtensions.Should().Contain("png");
        stream.Position.Should().Be(3);
    }

    [Fact]
    public async Task ClassifyAsync_EmptyContentUsesOfficialEmptyLabel()
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.X86)
            return;

        using var model = new MagikaContentTypeClassificationModel();
        await using var stream = new MemoryStream();

        var result = await model.ClassifyAsync(stream);

        result.Status.Should().Be(ContentTypeClassificationStatus.Detected);
        result.Label.Should().Be("empty");
    }

    [Theory]
    [InlineData("SGVsbG8=", "txt")]
    [InlineData("//79/A==", "unknown")]
    public async Task ClassifyAsync_ShortContentUsesTextFallback(string base64, string expectedLabel)
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.X86)
            return;

        using var model = new MagikaContentTypeClassificationModel();
        await using var stream = new MemoryStream(Convert.FromBase64String(base64));

        var result = await model.ClassifyAsync(stream);

        result.Status.Should().Be(ContentTypeClassificationStatus.Detected);
        result.Label.Should().Be(expectedLabel);
    }

    [Fact]
    public async Task ClassifyAsync_ConcurrentFirstUseProducesConsistentResults()
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.X86)
            return;

        using var model = new MagikaContentTypeClassificationModel();
        var operations = Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var stream = new MemoryStream("plain text attachment"u8.ToArray());
            return await model.ClassifyAsync(stream);
        });

        var results = await Task.WhenAll(operations);

        results.Should().OnlyContain(result =>
            result.Status == ContentTypeClassificationStatus.Detected && result.Label == "txt");
    }

    [Fact]
    public async Task ClassifyAsync_CancelledWaiterDoesNotPoisonSharedInitialization()
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.X86)
            return;

        using var model = new MagikaContentTypeClassificationModel();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var cancelledStream = new MemoryStream("first request"u8.ToArray());

        var action = async () => await model.ClassifyAsync(cancelledStream, cancellation.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();

        await using var succeedingStream = new MemoryStream("second request"u8.ToArray());
        var result = await model.ClassifyAsync(succeedingStream);
        result.Status.Should().Be(ContentTypeClassificationStatus.Detected);
        result.Label.Should().Be("txt");
    }

    [Fact]
    public async Task ClassifyAsync_CorruptConfigurationReturnsFailed()
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.X86)
            return;

        var directory = Path.Combine(Path.GetTempPath(), $"wino-magika-corrupt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "config.min.json"), "{}");
            using var model = new MagikaContentTypeClassificationModel(directory);
            await using var stream = new MemoryStream("content"u8.ToArray());

            var result = await model.ClassifyAsync(stream);

            result.Status.Should().Be(ContentTypeClassificationStatus.Failed);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ClassifyAsync_CorruptModelChecksumReturnsFailed()
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.X86)
            return;

        var directory = Path.Combine(Path.GetTempPath(), $"wino-magika-checksum-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            File.Copy(Path.Combine(MagikaAssetLoader.GetDefaultModelDirectory(), "config.min.json"), Path.Combine(directory, "config.min.json"));
            File.Copy(Path.Combine(MagikaAssetLoader.GetDefaultModelDirectory(), "content_types_kb.min.json"), Path.Combine(directory, "content_types_kb.min.json"));
            await File.WriteAllBytesAsync(Path.Combine(directory, "model.onnx"), [1, 2, 3, 4]);
            using var model = new MagikaContentTypeClassificationModel(directory);
            await using var stream = new MemoryStream("content"u8.ToArray());

            var result = await model.ClassifyAsync(stream);

            result.Status.Should().Be(ContentTypeClassificationStatus.Failed);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ClassifyAsync_AfterDisposalThrows()
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.X86)
            return;

        var model = new MagikaContentTypeClassificationModel();
        model.Dispose();
        await using var stream = new MemoryStream("content"u8.ToArray());

        var action = async () => await model.ClassifyAsync(stream);

        await action.Should().ThrowAsync<ObjectDisposedException>();
    }

    private static MagikaContentTypeClassificationModel CreateModel() => new();
}
