using Wino.Core.Domain.Enums;

namespace Wino.Mail.WinUI.Activation;

internal static class AppModeActivationResolver
{
    public static WinoApplicationMode Resolve(string? launchArguments, string? tileId, string? appId, WinoApplicationMode defaultMode = WinoApplicationMode.Mail)
        => Wino.Core.Activation.AppModeActivationResolver.Resolve(launchArguments, tileId, appId, defaultMode);
}
