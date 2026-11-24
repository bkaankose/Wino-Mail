using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Messaging.Client.Shell;
using Wino.Core.Domain.Translations;
using Wino.Mail.ViewModels.Lifecycle;
using Wino.Platform.Linux;
using Wino.Services;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>
/// The app language before the container exists, so the keyring and startup errors are translated: the stored
/// preference when there is one, else the session locale (<c>LC_ALL</c>, then <c>LC_MESSAGES</c>, then <c>LANG</c>;
/// .NET reads only <c>LANG</c> and <c>LC_ALL</c>). The shared <c>TranslationService</c> takes over once services run. The
/// text direction follows the app language, not the session (GTK picks it from the locale during its own start).
/// </summary>
internal static class LinuxLanguage
{
    private const string PreferenceKey = nameof(IPreferencesService.CurrentLanguage);

    public static AppLanguage Prepared { get; private set; } = AppLanguage.English;

    /// <summary>Runs first in <c>Main</c>, before any <see cref="Translator"/> text is read.</summary>
    public static void Prepare()
    {
        if (SessionCulture() is { } session && !string.Equals(session.Name, CultureInfo.CurrentUICulture.Name, StringComparison.OrdinalIgnoreCase))
        {
            CultureInfo.DefaultThreadCurrentUICulture = session;
            CultureInfo.CurrentUICulture = session;
        }

        var language = StoredLanguage() ?? Resolve(CultureInfo.CurrentUICulture);
        try
        {
            Load(AppLanguage.English);
            if (language != AppLanguage.English) Load(language);
            Prepared = language;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException)
        {
            Serilog.Log.Warning(exception, "The {Language} strings could not be loaded before startup.", language);
        }
    }

    /// <summary>Main thread, after GTK started. Right to left for Hebrew; GTK re-lays out every existing window.</summary>
    public static void ApplyDirection(AppLanguage language)
        => Gtk.Widget.SetDefaultDirection(IsRightToLeft(language) ? Gtk.TextDirection.Rtl : Gtk.TextDirection.Ltr);

    public static bool IsRightToLeft(AppLanguage language)
    {
        try { return new CultureInfo(WinoTranslationDictionary.GetLanguageFileNameRelativePath(language).Replace('_', '-')).TextInfo.IsRightToLeft; }
        catch (CultureNotFoundException) { return false; }
    }

    internal static CultureInfo? SessionCulture()
    {
        foreach (var name in (string[])["LC_ALL", "LC_MESSAGES", "LANG"])
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value)) continue;
            var tag = value.Split('.', '@')[0].Replace('_', '-');
            if (tag is "C" or "POSIX" || tag.Length == 0) return null;
            try { return CultureInfo.GetCultureInfo(tag); }
            catch (CultureNotFoundException) { return null; }
        }
        return null;
    }

    /// <summary>The shared resolver's rule (<c>TranslationService.ResolveSupportedLanguage</c> is internal): language code first, Traditional Chinese by script or region.</summary>
    internal static AppLanguage Resolve(CultureInfo culture)
    {
        var code = culture.TwoLetterISOLanguageName;
        if (code == "zh")
        {
            var name = culture.Name.ToUpperInvariant();
            var traditional = !name.Contains("HANS", StringComparison.Ordinal)
                              && (name.Contains("HANT", StringComparison.Ordinal) || name.EndsWith("-TW", StringComparison.Ordinal)
                                  || name.EndsWith("-HK", StringComparison.Ordinal) || name.EndsWith("-MO", StringComparison.Ordinal));
            return traditional ? AppLanguage.ChineseTraditional : AppLanguage.Chinese;
        }

        foreach (var language in Enum.GetValues<AppLanguage>())
        {
            if (language is AppLanguage.None or AppLanguage.ChineseTraditional) continue;
            var file = WinoTranslationDictionary.GetLanguageFileNameRelativePath(language);
            if (file.StartsWith(code + "_", StringComparison.OrdinalIgnoreCase)) return language;
        }
        return AppLanguage.English;
    }

    private static AppLanguage? StoredLanguage()
    {
        try
        {
            var root = LinuxPaths.FromEnvironment().ApplicationDataRoot;
            // Never create the data folder here: its absence marks a new installation (see WindowStateStore.LoadPlacement).
            if (!File.Exists(Path.Combine(root, "preferences.json"))) return null;
            var stored = new JsonFileConfigurationService(root).Get(PreferenceKey, AppLanguage.None);
            return stored == AppLanguage.None ? null : stored;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Serilog.Log.Warning(exception, "The stored app language could not be read.");
            return null;
        }
    }

    private static void Load(AppLanguage language) => Translator.Resources.Load(language);
}

/// <summary>Keeps the text direction on the app language: once the shared service applied the stored preference, and on every change.</summary>
internal sealed class LanguageHook(IMessenger messenger, IDispatcher dispatcher, ITranslationService translation) : IApplicationLifecycleHook, IRecipient<LanguageChanged>
{
    public Task ServicesReadyAsync()
    {
        messenger.Register<LanguageChanged>(this);
        return ApplyAsync();
    }

    public Task StoppingAsync(bool runtimeStarted)
    {
        messenger.Unregister<LanguageChanged>(this);
        return Task.CompletedTask;
    }

    public void Receive(LanguageChanged message) => _ = ApplyAsync();

    private async Task ApplyAsync()
    {
        try
        {
            var language = translation.CurrentLanguageModel?.Language ?? LinuxLanguage.Prepared;
            await dispatcher.ExecuteOnUIThread(() => LinuxLanguage.ApplyDirection(language));
        }
        catch (Exception exception) { Serilog.Log.Error(exception, "The text direction could not be applied."); }
    }
}
