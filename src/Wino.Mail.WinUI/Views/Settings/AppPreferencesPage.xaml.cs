using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.WinUI;
using Wino.Mail.WinUI.Services;
using Wino.Views.Abstract;

namespace Wino.Views.Settings;

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
