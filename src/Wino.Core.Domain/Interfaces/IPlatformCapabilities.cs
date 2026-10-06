namespace Wino.Core.Domain.Interfaces;

public interface IPlatformCapabilities
{
    bool Printing { get; }
    bool PdfExport { get; }
    bool Smime { get; }
    bool AdditionalWindows { get; }
    bool Notifications { get; }
    bool NotificationActions { get; }
    bool StartupIntegration { get; }
    bool Tray { get; }
    bool GlobalHotkeys { get; }
    bool GeneralActivation { get; }
    bool MicrosoftStore { get; }
}
