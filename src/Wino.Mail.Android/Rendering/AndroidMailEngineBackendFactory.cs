using Wino.Mail.Rendering;
using Wino.Renderer.Platform;
using Wino.Renderer.Platform.Android;

namespace Wino.Mail.Android.Rendering;

/// <summary>android.graphics Canvas, Minikin text and BitmapFactory: the Android backend of the mail engine.</summary>
public sealed class AndroidMailEngineBackendFactory : IMailEngineBackendFactory
{
    public IPlatformBackend Create() => AndroidBackend.Create(global::Android.App.Application.Context);

    public string Describe(IPlatformBackend backend) => AndroidBackend.Describe(backend);
}
