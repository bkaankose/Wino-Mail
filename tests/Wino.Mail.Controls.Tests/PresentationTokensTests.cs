using FluentAssertions;
using Wino.Presentation;
using Xunit;

namespace Wino.Mail.Controls.Tests;

public sealed class PresentationTokensTests
{
    [Theory]
    [InlineData("#E74C3C", 0xE7, 0x4C, 0x3C, 1.0)]
    [InlineData("  80FFFFFF ", 0xFF, 0xFF, 0xFF, 128 / 255.0)]
    public void FromHexString_ParsesRgbAndArgb(string value, int r, int g, int b, double alpha)
        => WinoRgba.FromHexString(value).Should().Be(new WinoRgba((byte)r, (byte)g, (byte)b, alpha));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#FFF")]
    [InlineData("#GGGGGG")]
    public void FromHexString_RejectsOtherInput(string? value)
        => WinoRgba.FromHexString(value).Should().BeNull();

    [Fact]
    public void AvatarPalette_ParsesAndIndexesStably()
    {
        WinoTokens.AvatarPalette.Should().HaveCount(16);
        WinoTokens.AvatarPalette.Select(WinoRgba.Parse).Should().HaveCount(16);
        WinoTokens.AvatarPaletteIndex(null).Should().Be(14);
        WinoTokens.AvatarPaletteIndex("Ada@Example.com").Should().Be(WinoTokens.AvatarPaletteIndex("ada@example.com"))
            .And.BeInRange(0, 15);
    }
}
