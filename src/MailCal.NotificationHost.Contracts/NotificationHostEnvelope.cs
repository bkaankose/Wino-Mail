namespace MailCal.NotificationHost.Contracts;

public sealed record NotificationHostActivation(
    DateTimeOffset CreatedAtUtc,
    NotificationHostApplication Application,
    string Argument,
    IReadOnlyDictionary<string, string> UserInput);
