#nullable enable
namespace Wino.Core.Domain.Models.Notifications;

/// <summary>A notification sound a platform offers. <see cref="Id"/> is what the sound provider stores.</summary>
public sealed record NotificationSoundChoice(string Id, string DisplayName)
{
    /// <summary>The platform's default notification sound.</summary>
    public const string DefaultId = "default";

    /// <summary>No sound.</summary>
    public const string NoneId = "none";

    public bool IsDefaultOrNone => Id is DefaultId or NoneId;
}
