using System;
using System.Linq;
using FluentAssertions;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Settings;
using Xunit;

namespace Wino.Core.Tests.Models;

public class WinoIconGlyphTests
{
    [Fact]
    public void Every_Icon_Maps_To_A_Private_Use_Glyph()
    {
        foreach (var icon in Enum.GetValues<WinoIconGlyph>().Where(i => i != WinoIconGlyph.None))
        {
            var glyph = WinoIconGlyphs.GetGlyph(icon);
            var codepoint = char.ConvertToUtf32(glyph, 0);

            var isPrivateUse = codepoint is >= 0xE000 and <= 0xF8FF or >= 0xF0000 and <= 0xFFFFD;
            isPrivateUse.Should().BeTrue($"{icon} maps to U+{codepoint:X}, which is outside the private use areas");
        }
    }

    [Fact]
    public void None_Maps_To_A_Blank_Glyph()
    {
        WinoIconGlyphs.GetGlyph(WinoIconGlyph.None).Should().Be(" ");
    }

    [Fact]
    public void Folder_Icons_Are_Separate_From_Command_Icons()
    {
        // Colorful mode tints folder icons; sharing a glyph with a command would tint toolbars too.
        WinoIconGlyphs.GetGlyph(WinoIconGlyph.SpecialFolderDeleted).Should().NotBe(WinoIconGlyphs.GetGlyph(WinoIconGlyph.Delete));
        WinoIconGlyphs.GetGlyph(WinoIconGlyph.SpecialFolderSent).Should().NotBe(WinoIconGlyphs.GetGlyph(WinoIconGlyph.Send));
        WinoIconGlyphs.GetGlyph(WinoIconGlyph.SpecialFolderArchive).Should().NotBe(WinoIconGlyphs.GetGlyph(WinoIconGlyph.Archive));
        WinoIconGlyphs.GetGlyph(WinoIconGlyph.SpecialFolderJunk).Should().NotBe(WinoIconGlyphs.GetGlyph(WinoIconGlyph.Blocked));
    }

    [Fact]
    public void Every_Settings_Navigation_Item_Has_An_Icon()
    {
        SettingsNavigationInfoProvider.GetNavigationItems()
            .Should().OnlyContain(item => item.Icon != WinoIconGlyph.None);
    }
}
