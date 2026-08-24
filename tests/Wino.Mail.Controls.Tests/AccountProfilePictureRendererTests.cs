#if WINDOWS
using FluentAssertions;
using SkiaSharp;
using Wino.Mail.Controls.AccountIcon;
using Xunit;

namespace Wino.Mail.Controls.Tests;

public sealed class AccountProfilePictureRendererTests
{
    [Fact]
    public void Render_CenterCropsAndMakesCornersTransparent()
    {
        var source = CreateSplitImage(width: 120, height: 60);

        var result = AccountProfilePictureRenderer.Render(source, null);

        using var bitmap = SKBitmap.Decode(result);
        bitmap.Width.Should().Be(AccountProfilePictureRenderer.IconPixelSize);
        bitmap.Height.Should().Be(AccountProfilePictureRenderer.IconPixelSize);
        bitmap.GetPixel(0, 0).Alpha.Should().Be(0);
        var centerPixel = bitmap.GetPixel(24, 24);
        centerPixel.Green.Should().BeGreaterThan(centerPixel.Red);
        centerPixel.Green.Should().BeGreaterThan(centerPixel.Blue);
    }

    [Fact]
    public void Render_ValidColorDrawsBorder()
    {
        var result = AccountProfilePictureRenderer.Render(CreateSolidImage(), "#FF0000");

        using var bitmap = SKBitmap.Decode(result);
        bitmap.GetPixel(24, 1).Should().Be(SKColors.Red);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-color")]
    public void Render_MissingOrInvalidColorDoesNotDrawBorder(string? accountColorHex)
    {
        var result = AccountProfilePictureRenderer.Render(CreateSolidImage(), accountColorHex);

        using var bitmap = SKBitmap.Decode(result);
        var edgePixel = bitmap.GetPixel(24, 1);
        edgePixel.Blue.Should().BeGreaterThan(edgePixel.Green);
        edgePixel.Green.Should().BeGreaterThan(edgePixel.Red);
    }

    [Fact]
    public void GetCacheKey_IsDeterministicAndIncludesColor()
    {
        var image = CreateSolidImage();

        var first = AccountProfilePictureRenderer.GetCacheKey(image, "#336699");
        var second = AccountProfilePictureRenderer.GetCacheKey(image, " #336699 ");
        var withoutColor = AccountProfilePictureRenderer.GetCacheKey(image, null);

        first.Should().Be(second);
        first.Should().NotBe(withoutColor);
    }

    [Fact]
    public void Render_InvalidImageThrowsArgumentException()
    {
        var action = () => AccountProfilePictureRenderer.Render([1, 2, 3], null);

        action.Should().Throw<ArgumentException>();
    }

    private static byte[] CreateSolidImage()
    {
        using var bitmap = new SKBitmap(96, 96);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.CornflowerBlue);
        return Encode(bitmap);
    }

    private static byte[] CreateSplitImage(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Green);
        using var edgePaint = new SKPaint { Color = SKColors.Red };
        canvas.DrawRect(0, 0, 25, height, edgePaint);
        canvas.DrawRect(width - 25, 0, 25, height, edgePaint);
        return Encode(bitmap);
    }

    private static byte[] Encode(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
#endif
