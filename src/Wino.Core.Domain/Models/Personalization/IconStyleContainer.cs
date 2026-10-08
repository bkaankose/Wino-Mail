using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Personalization;

public class IconStyleContainer
{
    public IconStyleContainer(WinoIconStyle iconStyle, string title)
    {
        IconStyle = iconStyle;
        Title = title;
    }

    public WinoIconStyle IconStyle { get; }
    public string Title { get; }
}
