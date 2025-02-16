using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Settings
{
    public record SettingOption(string Title, string Description, WinoPage NavigationPage, string PathIcon);
}
