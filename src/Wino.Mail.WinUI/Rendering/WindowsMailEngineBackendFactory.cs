using Wino.Mail.Rendering;
using Wino.Renderer.Platform;
using Wino.Renderer.Platform.Windows;

namespace Wino.Mail.WinUI.Rendering;

/// <summary>Direct2D, DirectWrite and WIC: the Windows backend of the mail engine.</summary>
public sealed class WindowsMailEngineBackendFactory : IMailEngineBackendFactory
{
    public IPlatformBackend Create() => WindowsBackend.Create();

    public string Describe(IPlatformBackend backend) => WindowsBackend.Describe(backend);
}
