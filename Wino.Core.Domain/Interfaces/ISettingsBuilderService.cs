using System.Collections.Generic;
using Wino.Core.Domain.Models.Settings;

namespace Wino.Core.Domain.Interfaces
{
    public interface ISettingsBuilderService
    {
        List<SettingOption> GetSettingItems();
    }
}
