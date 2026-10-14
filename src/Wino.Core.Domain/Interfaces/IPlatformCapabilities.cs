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
    bool AppleAppStore { get; }

    /// <summary>The platform can receive relay pushes (APNs). Live notifications are always on when signed in unless <see cref="PushRelayOptIn"/>.</summary>
    bool RemoteNotifications { get; }

    /// <summary>Live notifications are an opt-in setting on this platform (off by default).</summary>
    bool PushRelayOptIn { get; }
}
