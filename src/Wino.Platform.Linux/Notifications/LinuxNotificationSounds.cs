using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Notifications;
using Wino.Platform.Linux.Native;

namespace Wino.Platform.Linux.Notifications;

/// <summary>
/// Notification sounds as freedesktop sound-theme events played through libcanberra (Apple AppleNotificationSounds
/// port). Windows stores a <see cref="NotificationSoundEvent"/> per type and per account; Linux keeps its own keys with
/// an event id, "default" or "none", and maps the shared value while a key is unset. The sound plays when a
/// notification is shown (after the notification policy let it through); GNOME's do-not-disturb is not readable over a
/// freedesktop interface, so it does not silence it.
/// </summary>
public sealed class LinuxNotificationSounds(IConfigurationService configuration, IPreferencesService preferences) : INotificationSoundProvider
{
    public const string DefaultSound = NotificationSoundChoice.DefaultId;
    public const string NoSound = NotificationSoundChoice.NoneId;

    private const string KeyPrefix = "LinuxNotificationSound.";
    private const string AccountKeyPrefix = KeyPrefix + "Account.";
    private static readonly TimeSpan NotificationGap = TimeSpan.FromSeconds(1);

    // Events of the freedesktop sound theme that Ubuntu's Yaru also ships or inherits.
    private static readonly string[] ThemeEvents =
    [
        "message-new-email", "message-new-instant", "message", "bell", "complete",
        "dialog-information", "dialog-warning", "window-attention", "alarm-clock-elapsed",
    ];

    private NotificationSoundChoice[]? _choices;
    private long _lastNotificationSound;

    public event EventHandler? Changed;

    public IReadOnlyList<NotificationSoundChoice> Choices => _choices ??=
    [
        new(DefaultSound, Translator.NotificationSound_Default),
        new(NoSound, Translator.ContactsShellMac_NotificationSoundNone),
        .. ThemeEvents.Select(static id => new NotificationSoundChoice(id, id)),
    ];

    public string GetSound(NotificationSoundKind kind) => Resolve(kind);

    public void SetSound(NotificationSoundKind kind, string id)
    {
        configuration.Set(KeyPrefix + kind, id ?? string.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public string GetAccountSound(Guid accountId) => configuration.Get(AccountKey(accountId), string.Empty) ?? string.Empty;

    public void SetAccountSound(Guid accountId, string? id)
    {
        configuration.Set(AccountKey(accountId), id ?? string.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public string MapSoundEvent(NotificationSoundEvent soundEvent) => Map(soundEvent);

    public void ResetAll(IEnumerable<Guid> accountIds)
    {
        foreach (var kind in (NotificationSoundKind[])[NotificationSoundKind.Mail, NotificationSoundKind.Calendar, NotificationSoundKind.Task])
            configuration.Remove(KeyPrefix + kind);
        foreach (var accountId in accountIds) configuration.Remove(AccountKey(accountId));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task PlayAsync(string id) => Task.Run(() => Play(id, NotificationSoundKind.Mail));

    /// <summary>The sound in effect for a type: the stored choice, otherwise the shared value mapped.</summary>
    public string Resolve(NotificationSoundKind kind)
    {
        var stored = configuration.Get(KeyPrefix + kind, string.Empty);
        if (!string.IsNullOrEmpty(stored)) return stored;
        return Map(kind switch
        {
            NotificationSoundKind.Mail => preferences.MailNotificationSoundEvent,
            NotificationSoundKind.Calendar => preferences.CalendarNotificationSoundEvent,
            _ => preferences.TaskNotificationSoundEvent,
        });
    }

    /// <summary>The mail sound for an account: its override when it has its own settings, otherwise the app-wide one.</summary>
    public string ResolveMail(MailAccountPreferences? account)
    {
        if (account is null || !account.IsNotificationsEnabled || !account.HasCustomNotificationSettings) return Resolve(NotificationSoundKind.Mail);
        var stored = GetAccountSound(account.AccountId);
        return string.IsNullOrEmpty(stored) ? Map(account.AccountNotificationSoundEvent) : stored;
    }

    public static string Map(NotificationSoundEvent soundEvent) => soundEvent switch
    {
        NotificationSoundEvent.Mail => "message-new-email",
        NotificationSoundEvent.Reminder => "alarm-clock-elapsed",
        NotificationSoundEvent.IM => "message-new-instant",
        NotificationSoundEvent.SMS => "message",
        NotificationSoundEvent.None => NoSound,
        _ => DefaultSound,
    };

    /// <summary>The theme event a sound id plays for a type; null for none.</summary>
    public static string? EventFor(string id, NotificationSoundKind? kind)
    {
        if (string.Equals(id, NoSound, StringComparison.OrdinalIgnoreCase)) return null;
        if (!string.IsNullOrEmpty(id) && !string.Equals(id, DefaultSound, StringComparison.OrdinalIgnoreCase)) return id;
        return kind switch
        {
            NotificationSoundKind.Mail => "message-new-email",
            NotificationSoundKind.Calendar => "alarm-clock-elapsed",
            NotificationSoundKind.Task => "complete",
            _ => "message",
        };
    }

    /// <summary>Plays a sound id; blocks only for the hand-over to the sound server. False when nothing played.</summary>
    public bool Play(string id, NotificationSoundKind? kind)
    {
        if (EventFor(id, kind) is not { } eventId) return false;
        return Canberra.TryPlay(eventId, Translator.NotificationSound_Title);
    }

    /// <summary>The sound of a notification that is being shown; several notifications of one batch sound once.</summary>
    public void PlayForNotification(string id, NotificationSoundKind? kind)
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastNotificationSound) < NotificationGap.TotalMilliseconds) return;
        Interlocked.Exchange(ref _lastNotificationSound, now);
        _ = Task.Run(() => Play(id, kind));
    }

    private static string AccountKey(Guid accountId) => AccountKeyPrefix + accountId.ToString("N");
}
