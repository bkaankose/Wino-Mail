using Wino.Core.Domain.Interfaces;
using Wino.Mail.ViewModels.Common;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>
/// The shared appearance model refreshed by <see cref="WinoAppearance.Changed"/>, which the theme service raises when a
/// Wino theme starts or stops forcing light or dark (the shared model only hears root theme and accent changes).
/// </summary>
internal sealed class LinuxAppearanceModel : AppearanceSettingsModel
{
    public LinuxAppearanceModel(INewThemeService themes) : base(themes)
        => WinoAppearance.Changed.Add(this, static self => self.NotifyAppearanceChanged());

    public override void Dispose()
    {
        WinoAppearance.Changed.Remove(this);
        base.Dispose();
    }
}
