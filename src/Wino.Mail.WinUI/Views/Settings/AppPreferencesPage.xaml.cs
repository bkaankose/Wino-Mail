using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.WinUI;
using Wino.Mail.WinUI.Services;
using Wino.Mail.WinUI.Views.Abstract;

namespace Wino.Mail.WinUI.Views.Settings;

public sealed partial class AppPreferencesPage : AppPreferencesPageAbstract
{
    /// <summary>Windows-only shell preferences (the taskbar identity per app mode).</summary>
    public WindowsShellPreferences ShellPreferences { get; } = WinoApplication.Current.Services.GetRequiredService<WindowsShellPreferences>();

    public AppPreferencesPage()
    {
        this.InitializeComponent();
    }

    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();

        Bindings.Update();
    }
}
