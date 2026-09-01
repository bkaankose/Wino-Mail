namespace Wino.NotificationHost.Contracts;

public sealed record NotificationHostRequest(
    DateTimeOffset CreatedAtUtc,
    NotificationHostOperation Operation,
    NotificationHostApplication Application,
    string? Payload,
    string? Tag,
    string? Group);

public sealed record NotificationHostActivation(
    DateTimeOffset CreatedAtUtc,
    NotificationHostApplication Application,
    string Argument,
    IReadOnlyDictionary<string, string> UserInput);
