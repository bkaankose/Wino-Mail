using AppKit;
using Foundation;
using UserNotifications;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Notifications;

namespace Wino.Platform.MacOS.Services;

/// <summary>The notification types that carry their own sound choice.</summary>
public enum MacNotificationSoundKind
{
    Mail,
    Calendar,
    Task
}

/// <summary>
/// macOS notification sounds. Windows stores a <see cref="NotificationSoundEvent"/> per type and per
/// account; Mac never writes those values. It keeps its own local (non-roaming) keys holding a macOS
/// alert sound name, "default" or "none". While a Mac key is unset, the shared enum value is mapped
/// onto a system sound. Notification sounds must come from the app container, so a chosen system
/// sound is copied once, in the background, into the container's Library/Sounds.
/// </summary>
public sealed class MacNotificationSounds
{
    public const string DefaultSound = "default";
    public const string NoSound = "none";

    private const string KeyPrefix = "MacNotificationSound.";
    private const string AccountKeyPrefix = KeyPrefix + "Account.";
    private const string SystemSoundsFolder = "/System/Library/Sounds";

    private static readonly Lazy<IReadOnlyList<string>> SystemSoundList = new(LoadSystemSounds);
    private readonly IConfigurationService _configuration;
    private readonly IPreferencesService _preferences;
    private readonly object _copyLock = new();
    private readonly HashSet<string> _copying = new(StringComparer.OrdinalIgnoreCase);

    public MacNotificationSounds(IConfigurationService configuration, IPreferencesService preferences)
    {
        _configuration = configuration;
        _preferences = preferences;
    }

    /// <summary>The macOS alert sounds (the .aiff files in /System/Library/Sounds), sorted; read once per process.</summary>
    public static IReadOnlyList<string> SystemSounds => SystemSoundList.Value;

    /// <summary>The container folder notification sounds are looked up in.</summary>
    public static string ContainerSoundsFolder => Path.Combine(NSFileManager.HomeDirectory, "Library", "Sounds");

    #region Stored choices

    private static string TypeKey(MacNotificationSoundKind kind) => KeyPrefix + kind;
    private static string AccountKey(Guid accountId) => AccountKeyPrefix + accountId.ToString("N");

    /// <summary>The Mac choice stored for a type; empty when the shared enum value applies.</summary>
    public string GetStored(MacNotificationSoundKind kind) => _configuration.Get(TypeKey(kind), string.Empty) ?? string.Empty;

    public void SetStored(MacNotificationSoundKind kind, string sound)
    {
        _configuration.Set(TypeKey(kind), sound ?? string.Empty);
        Prepare(sound);
    }

    /// <summary>The Mac choice stored for an account's override; empty when the account's enum value applies.</summary>
    public string GetStoredForAccount(Guid accountId) => _configuration.Get(AccountKey(accountId), string.Empty) ?? string.Empty;

    public void SetStoredForAccount(Guid accountId, string sound)
    {
        _configuration.Set(AccountKey(accountId), sound ?? string.Empty);
        Prepare(sound);
    }

    public void ResetAccount(Guid accountId) => _configuration.Remove(AccountKey(accountId));

    /// <summary>Removes every Mac sound choice (notification settings reset).</summary>
    public void ResetAll(IEnumerable<Guid> accountIds)
    {
        foreach (var kind in Enum.GetValues<MacNotificationSoundKind>()) _configuration.Remove(TypeKey(kind));
        foreach (var accountId in accountIds) ResetAccount(accountId);
    }

    #endregion

    #region Resolution

    /// <summary>The sound in effect for a type: the Mac choice, otherwise the shared enum value mapped.</summary>
    public string Resolve(MacNotificationSoundKind kind)
    {
        var stored = GetStored(kind);
        if (!string.IsNullOrEmpty(stored)) return stored;
        return Map(kind switch
        {
            MacNotificationSoundKind.Mail => _preferences.MailNotificationSoundEvent,
            MacNotificationSoundKind.Calendar => _preferences.CalendarNotificationSoundEvent,
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
            return Resolve(MacNotificationSoundKind.Mail);

        return ResolveAccount(account);
    }

    /// <summary>The account override's sound: the Mac choice, otherwise the account's enum value mapped.</summary>
    public string ResolveAccount(MailAccountPreferences account)
    {
        var stored = GetStoredForAccount(account.AccountId);
        return string.IsNullOrEmpty(stored) ? Map(account.AccountNotificationSoundEvent) : stored;
    }

    /// <summary>The macOS sound used for a Windows sound event while no Mac choice is stored.</summary>
    public static string Map(NotificationSoundEvent soundEvent) => soundEvent switch
    {
        NotificationSoundEvent.Mail => "Glass",
        NotificationSoundEvent.Reminder => "Ping",
        NotificationSoundEvent.IM => "Pop",
        NotificationSoundEvent.SMS => "Tink",
        _ => DefaultSound
    };

    public static bool IsSystemSound(string? sound)
        => !string.IsNullOrEmpty(sound) && SystemSounds.Contains(sound, StringComparer.OrdinalIgnoreCase);

    #endregion

    #region Delivery and preview

    /// <summary>
    /// The notification sound for <paramref name="sound"/>: null for "none", the system default for "default",
    /// and the container copy of a system sound once it exists. Never blocks; until the copy is in place
    /// (or when it fails) the default sound plays.
    /// </summary>
    public UNNotificationSound? CreateNotificationSound(string sound)
    {
        if (string.Equals(sound, NoSound, StringComparison.OrdinalIgnoreCase)) return null;
        if (!IsSystemSound(sound)) return UNNotificationSound.Default;
        try
        {
            var fileName = sound + ".aiff";
            if (File.Exists(Path.Combine(ContainerSoundsFolder, fileName))) return UNNotificationSound.GetSound(fileName);
            Prepare(sound);
        }
        catch (Exception error)
        {
            Serilog.Log.Debug(error, "Notification sound {Sound} is unavailable.", sound);
        }
        return UNNotificationSound.Default;
    }

    /// <summary>Copies a system sound into the container in the background, once.</summary>
    public void Prepare(string? sound)
    {
        if (!IsSystemSound(sound)) return;
        var name = sound!;
        var target = Path.Combine(ContainerSoundsFolder, name + ".aiff");
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
                File.Copy(Path.Combine(SystemSoundsFolder, name + ".aiff"), temporary, overwrite: true);
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
        foreach (var kind in Enum.GetValues<MacNotificationSoundKind>()) Prepare(Resolve(kind));
    }

    /// <summary>Plays a preview: the named system sound, the alert sound for "default", nothing for "none". Main thread only.</summary>
    public static void Play(string? sound)
    {
        if (string.Equals(sound, NoSound, StringComparison.OrdinalIgnoreCase)) return;
        if (!IsSystemSound(sound))
        {
            AppKitFramework.NSBeep();
            return;
        }
        var preview = NSSound.FromName(sound!);
        if (preview is null) { AppKitFramework.NSBeep(); return; }
        preview.Stop();
        preview.Play();
    }

    /// <summary>Diagnostics for the debug bridge.</summary>
    public string Describe(IEnumerable<MailAccountPreferences> accounts)
    {
        string Status(string sound) => IsSystemSound(sound)
            ? File.Exists(Path.Combine(ContainerSoundsFolder, sound + ".aiff")) ? "copied" : "pending"
            : "-";
        var parts = Enum.GetValues<MacNotificationSoundKind>()
            .Select(kind => $"{kind}={Resolve(kind)}(stored='{GetStored(kind)}',{Status(Resolve(kind))})")
            .Concat(accounts.Select(account => $"account {account.AccountId:N}={ResolveMail(account)}(stored='{GetStoredForAccount(account.AccountId)}',custom={account.HasCustomNotificationSettings})"));
        return $"system={SystemSounds.Count} folder={ContainerSoundsFolder} " + string.Join(" ", parts);
    }

    #endregion

    private static IReadOnlyList<string> LoadSystemSounds()
    {
        try
        {
            return Directory.EnumerateFiles(SystemSoundsFolder, "*.aiff")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Select(static name => name!)
                .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception error)
        {
            Serilog.Log.Warning(error, "Could not list the macOS alert sounds.");
            return [];
        }
    }
}
