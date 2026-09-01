namespace Wino.NotificationHost.Contracts;

public static class NotificationHostLaunchArguments
{
    public const string RequestSwitch = "--request";
    public const string ForwardedActivationSwitch = "--notification-activation";

    public static string CreateRequest(Guid requestId)
        => $"{RequestSwitch} {ValidateId(requestId):D}";

    public static string CreateForwardedActivation(Guid activationId)
        => $"{ForwardedActivationSwitch} {ValidateId(activationId):D}";

    public static bool TryParseForwardedActivation(string? arguments, out Guid activationId)
    {
        activationId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(arguments))
            return false;

        var tokens = arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = 0; index < tokens.Length; index++)
        {
            if (!string.Equals(tokens[index], ForwardedActivationSwitch, StringComparison.Ordinal))
                continue;

            return index + 1 < tokens.Length &&
                   Guid.TryParseExact(tokens[index + 1], "D", out activationId) &&
                   activationId != Guid.Empty;
        }

        return false;
    }

    private static Guid ValidateId(Guid id)
        => id == Guid.Empty ? throw new ArgumentException("Envelope ID cannot be empty.", nameof(id)) : id;
}
