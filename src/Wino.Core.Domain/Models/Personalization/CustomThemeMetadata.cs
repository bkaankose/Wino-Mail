using System;

namespace Wino.Core.Domain.Models.Personalization;

public class CustomThemeMetadata
{
    public Guid Id { get; set; }
    public string Name { get; set; }
    public string AccentColorHex { get; set; }
    public bool HasCustomAccentColor => !string.IsNullOrEmpty(AccentColorHex);
}
