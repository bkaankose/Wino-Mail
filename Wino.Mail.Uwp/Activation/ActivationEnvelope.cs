namespace Wino.Mail.Uwp.Activation;

public enum ActivationDeliveryState
{
    Pending = 0,
    Processing,
}

public enum WinoActivationKind
{
    Launch = 0,
    Protocol,
    File,
    Toast,
    Share,
}

public sealed record ShareActivationPayload(string Title, string Text, string? WebLink);

public enum ActivationTargetSurface
{
    Mail = 0,
    Calendar,
}

public sealed record ActivationEnvelope(
    int Version,
    Guid Id,
    long CreatedUtcTicks,
    ActivationTargetSurface TargetSurface,
    WinoActivationKind Kind,
    string Arguments,
    string[] SharedStorageTokens,
    ActivationDeliveryState DeliveryState,
    int DeliveryAttempt = 0)
{
    public bool IsExpired(DateTimeOffset now) =>
        now - new DateTimeOffset(CreatedUtcTicks, TimeSpan.Zero) > TimeSpan.FromDays(7);
}
