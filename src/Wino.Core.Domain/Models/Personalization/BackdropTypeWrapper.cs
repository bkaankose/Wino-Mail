using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Personalization;

public class BackdropTypeWrapper
{
    public WindowBackdropType BackdropType { get; set; }
    public string DisplayName { get; set; }

    public BackdropTypeWrapper(WindowBackdropType backdropType, string displayName)
    {
        BackdropType = backdropType;
        DisplayName = displayName;
    }

    public override string ToString()
    {
        return DisplayName;
    }
}