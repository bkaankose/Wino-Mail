using Wino.Renderer.Platform;
using Wino.Renderer.Platform.Apple;

namespace Wino.Mail.Rendering.Apple;

/// <summary>
/// Core Text, Core Graphics and ImageIO. Each head passes the backend it makes with its own spell checker and system
/// settings (the AppKit and UIKit view projects provide them).
/// </summary>
public sealed class AppleMailEngineBackendFactory(Func<IPlatformBackend> create) : IMailEngineBackendFactory
{
    public IPlatformBackend Create() => create();

    public string Describe(IPlatformBackend backend) => AppleBackend.Describe(backend);
}
