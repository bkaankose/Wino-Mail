using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Personalization;

public class IconSetContainer
{
    public IconSetContainer(WinoIconSet iconSet, string title)
    {
        IconSet = iconSet;
        Title = title;
    }

    public WinoIconSet IconSet { get; }
    public string Title { get; }
}
