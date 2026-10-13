using System.Diagnostics.CodeAnalysis;
using Foundation;
using UserNotifications;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Notifications;

namespace Wino.Platform.Apple.Services;

/// <summary>The notification types that carry their own sound choice.</summary>
public enum AppleNotificationSoundKind
{
    Mail,
    Calendar,
    Task
}

/// <summary>
/// Apple notification sounds. Windows stores a <see cref="NotificationSoundEvent"/> per type and per
/// account; the Apple apps never write those values. They keep their own local (non-roaming) keys holding
/// an alert sound name from the platform's <see cref="INotificationSoundCatalog"/>, "default" or "none".
/// While such a key is unset, the shared enum value is mapped onto a system sound. Notification sounds must
/// come from the app container, so a chosen sound is copied once, in the background, into the container's
/// Library/Sounds.
/// </summary>
public sealed class AppleNotificationSounds
{
    public const string DefaultSound = "default";
    public const string NoSound = "none";

    // The key names predate the iOS app; the Mac app's stored choices depend on them.
    private const string KeyPrefix = "MacNotificationSound.";
    private const string AccountKeyPrefix = KeyPrefix + "Account.";

    private readonly IConfigurationService _configuration;
    private readonly IPreferencesService _preferences;
    private readonly INotificationSoundCatalog _catalog;
    private readonly object _copyLock = new();
    private readonly HashSet<string> _copying = new(StringComparer.OrdinalIgnoreCase);

    public AppleNotificationSounds(IConfigurationService configuration, IPreferencesService preferences, INotificationSoundCatalog catalog)
    {
        _configuration = configuration;
        _preferences = preferences;
        _catalog = catalog;
    }

    /// <summary>The platform's alert sounds, sorted (on macOS, the .aiff files in /System/Library/Sounds).</summary>
    public IReadOnlyList<string> SystemSounds => _catalog.Sounds;

    /// <summary>The container folder notification sounds are looked up in.</summary>
    public static string ContainerSoundsFolder => Path.Combine(NSFileManager.HomeDirectory, "Library", "Sounds");

    #region Stored choices

    private static string TypeKey(AppleNotificationSoundKind kind) => KeyPrefix + kind;
    private static string AccountKey(Guid accountId) => AccountKeyPrefix + accountId.ToString("N");

    /// <summary>The Apple choice stored for a type; empty when the shared enum value applies.</summary>
    public string GetStored(AppleNotificationSoundKind kind) => _configuration.Get(TypeKey(kind), string.Empty) ?? string.Empty;

    public void SetStored(AppleNotificationSoundKind kind, string sound)
    {
        _configuration.Set(TypeKey(kind), sound ?? string.Empty);
        Prepare(sound);
    }

    /// <summary>The Apple choice stored for an account's override; empty when the account's enum value applies.</summary>
    public string GetStoredForAccount(Guid accountId) => _configuration.Get(AccountKey(accountId), string.Empty) ?? string.Empty;

    public void SetStoredForAccount(Guid accountId, string sound)
    {
        _configuration.Set(AccountKey(accountId), sound ?? string.Empty);
        Prepare(sound);
    }

    public void ResetAccount(Guid accountId) => _configuration.Remove(AccountKey(accountId));

    /// <summary>Removes every Apple sound choice (notification settings reset).</summary>
    public void ResetAll(IEnumerable<Guid> accountIds)
    {
        foreach (var kind in Enum.GetValues<AppleNotificationSoundKind>()) _configuration.Remove(TypeKey(kind));
        foreach (var accountId in accountIds) ResetAccount(accountId);
    }

    #endregion

    #region Resolution

    /// <summary>The sound in effect for a type: the Apple choice, otherwise the shared enum value mapped.</summary>
    public string Resolve(AppleNotificationSoundKind kind)
    {
        var stored = GetStored(kind);
        if (!string.IsNullOrEmpty(stored)) return stored;
        return Map(kind switch
        {
            AppleNotificationSoundKind.Mail => _preferences.MailNotificationSoundEvent,
            AppleNotificationSoundKind.Calendar => _preferences.CalendarNotificationSoundEvent,
            _ => _preferences.TaskNotificationSoundEvent
        });
    }

    /// <summary>
    /// The mail sound in effect for an account, following <see cref="NotificationSettingsResolver.ResolveMail"/>:
    /// an account with its own settings uses its override, otherwise the app-wide mail sound applies.
    /// </summary>
    public string ResolveMail(MailAccountPreferences? account)
    {
        if (account is null || !account.IsNotificationsEnabled || !account.HasCustomNotificationSettings)
            return Resolve(AppleNotificationSoundKind.Mail);

        return ResolveAccount(account);
    }

    /// <summary>The account override's sound: the Apple choice, otherwise the account's enum value mapped.</summary>
    public string ResolveAccount(MailAccountPreferences account)
    {
        var stored = GetStoredForAccount(account.AccountId);
        return string.IsNullOrEmpty(stored) ? Map(account.AccountNotificationSoundEvent) : stored;
    }

    /// <summary>
    /// The macOS alert sound used for a Windows sound event while no Apple choice is stored. A platform whose
    /// catalog does not list it plays the default sound.
    /// </summary>
    public static string Map(NotificationSoundEvent soundEvent) => soundEvent switch
    {
        NotificationSoundEvent.Mail => "Glass",
        NotificationSoundEvent.Reminder => "Ping",
        NotificationSoundEvent.IM => "Pop",
        NotificationSoundEvent.SMS => "Tink",
        _ => DefaultSound
    };

    public bool IsSystemSound([NotNullWhen(true)] string? sound)
        => !string.IsNullOrEmpty(sound) && SystemSounds.Contains(sound, StringComparer.OrdinalIgnoreCase);

    #endregion

    #region Delivery and preview

    /// <summary>The container copy of a catalog sound, or null when the catalog has no file for it.</summary>
    private string? ContainerFileName(string sound)
        => _catalog.GetSourcePath(sound) is { } source ? sound + Path.GetExtension(source) : null;

    /// <summary>
    /// The notification sound for <paramref name="sound"/>: null for "none", the system default for "default",
    /// and the container copy of a catalog sound once it exists. Never blocks; until the copy is in place
    /// (or when it fails) the default sound plays.
    /// </summary>
    public UNNotificationSound? CreateNotificationSound(string sound)
    {
        if (string.Equals(sound, NoSound, StringComparison.OrdinalIgnoreCase)) return null;
        if (!IsSystemSound(sound)) return UNNotificationSound.Default;
        try
        {
            if (ContainerFileName(sound) is { } fileName && File.Exists(Path.Combine(ContainerSoundsFolder, fileName)))
                return UNNotificationSound.GetSound(fileName);
            Prepare(sound);
        }
        catch (Exception error)
        {
            Serilog.Log.Debug(error, "Notification sound {Sound} is unavailable.", sound);
        }
        return UNNotificationSound.Default;
    }

    /// <summary>Copies a catalog sound into the container in the background, once.</summary>
    public void Prepare(string? sound)
    {
        if (!IsSystemSound(sound)) return;
        var name = sound;
        if (_catalog.GetSourcePath(name) is not { } source) return;
        var target = Path.Combine(ContainerSoundsFolder, name + Path.GetExtension(source));
        if (File.Exists(target)) return;
        lock (_copyLock)
        {
            if (!_copying.Add(name)) return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(ContainerSoundsFolder);
                var temporary = target + ".tmp";
                File.Copy(source, temporary, overwrite: true);
                File.Move(temporary, target, overwrite: true);
            }
            catch (Exception error)
            {
                Serilog.Log.Warning(error, "Could not prepare notification sound {Sound}.", name);
            }
            finally
            {
                lock (_copyLock) _copying.Remove(name);
            }
        });
    }

    /// <summary>Prepares every stored and mapped type sound, so the first notification already plays it.</summary>
    public void PrepareConfiguredSounds()
    {
        foreach (var kind in Enum.GetValues<AppleNotificationSoundKind>()) Prepare(Resolve(kind));
    }

    /// <summary>Plays a preview: the named catalog sound, the alert sound for "default", nothing for "none". UI thread only.</summary>
    public void Play(string? sound)
    {
        if (string.Equals(sound, NoSound, StringComparison.OrdinalIgnoreCase)) return;
        _catalog.Play(IsSystemSound(sound) ? sound : null);
    }

    /// <summary>Diagnostics for the debug bridge.</summary>
    public string Describe(IEnumerable<MailAccountPreferences> accounts)
    {
        string Status(string sound) => IsSystemSound(sound)
            ? ContainerFileName(sound) is { } fileName && File.Exists(Path.Combine(ContainerSoundsFolder, fileName)) ? "copied" : "pending"
            : "-";
        var parts = Enum.GetValues<AppleNotificationSoundKind>()
            .Select(kind => $"{kind}={Resolve(kind)}(stored='{GetStored(kind)}',{Status(Resolve(kind))})")
            .Concat(accounts.Select(account => $"account {account.AccountId:N}={ResolveMail(account)}(stored='{GetStoredForAccount(account.AccountId)}',custom={account.HasCustomNotificationSettings})"));
        return $"system={SystemSounds.Count} folder={ContainerSoundsFolder} " + string.Join(" ", parts);
    }

    #endregion
}
