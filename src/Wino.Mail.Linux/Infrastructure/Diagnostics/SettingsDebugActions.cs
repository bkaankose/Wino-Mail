#if DEBUG
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Linux.Views.Settings;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// Debug actions of the Settings window. <c>debug-settings</c> <c>s</c> (a <see cref="WinoPage"/> name, empty for the
/// default page), <c>debug-settings-message</c>, <c>debug-settings-back</c>, <c>debug-settings-close</c>,
/// <c>debug-settings-search</c> <c>s</c>, <c>debug-settings-screenshot</c> <c>s</c>, <c>debug-settings-resize</c>
/// <c>(ii)</c>, <c>debug-settings-dump</c> <c>s</c>, <c>debug-settings-state</c> <c>s</c>, <c>debug-settings-row</c>
/// <c>as</c> (<c>[title[#n], toggle|select|value|text|activate|expand, value?]</c>) and <c>debug-settings-pref</c>
/// <c>as</c> (<c>[name]</c> or <c>[name, value]</c>; each settings package adds its property names to the switch).
/// </summary>
internal static class SettingsDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        Add(application, "debug-settings", "s", parameter =>
        {
            var name = parameter.GetString(out _);
            WinoPage? page = null;
            if (!string.IsNullOrWhiteSpace(name))
            {
                if (!Enum.TryParse<WinoPage>(name, out var parsed)) throw new ArgumentException($"'{name}' is not a page.");
                page = parsed;
            }
            Observe("debug-settings", Presenter(services).ShowAsync(page, null));
        });

        Add(application, "debug-settings-message", () =>
            Required<IMailDialogService>(services).InfoBarMessage("Settings", "Message surface check", InfoBarMessageType.Information));

        Add(application, "debug-settings-back", () => Observe("debug-settings-back", Presenter(services).GoBackAsync()));
        Add(application, "debug-settings-close", () => Observe("debug-settings-close", Presenter(services).CloseAsync()));

        Add(application, "debug-settings-search", "s", parameter => Window(services).SetSearchText(parameter.GetString(out _)));

        Add(application, "debug-settings-screenshot", "s", parameter => DebugActions.Capture(Window(services).Window, parameter.GetString(out _)));

        Add(application, "debug-settings-resize", "(ii)", parameter =>
        {
            var width = parameter.GetChildValue(0).GetInt32();
            var height = parameter.GetChildValue(1).GetInt32();
            var window = Window(services).Window;
            if (window.IsMaximized()) window.Unmaximize();
            window.SetDefaultSize(width, height);
            Serilog.Log.Information("Debug action: Settings resized to {Width}x{Height}.", width, height);
        });

        Add(application, "debug-settings-dump", "s", parameter =>
        {
            var path = parameter.GetString(out _);
            DebugActions.WriteText(path, DebugActions.Describe(Window(services).Window));
            Serilog.Log.Information("Debug action: Settings tree written to {Path}.", path);
        });

        Add(application, "debug-settings-state", "s", parameter =>
        {
            var path = parameter.GetString(out _);
            var provider = services() ?? throw new InvalidOperationException("The app has not started.");
            var text = DescribeState(provider);
            DebugActions.WriteText(path, text);
            Serilog.Log.Information("Debug action: Settings state written to {Path}: {First}", path, text.Split('\n')[0]);
        });

        Add(application, "debug-settings-row", "as", parameter =>
        {
            var arguments = parameter.GetStrv(out _);
            if (arguments.Length < 2) throw new ArgumentException("Pass [title, operation, value?].");
            var page = Window(services).CurrentPageWidget ?? throw new InvalidOperationException("No Settings page is shown.");
            var result = DriveRow(page, arguments[0], arguments[1], arguments.Length > 2 ? arguments[2] : null);
            Serilog.Log.Information("Debug action: settings row '{Title}' {Operation} -> {Result}", arguments[0], arguments[1], result);
        });

        Add(application, "debug-settings-pref", "as", parameter =>
        {
            var arguments = parameter.GetStrv(out _);
            if (arguments.Length == 0) throw new ArgumentException("Pass [name] or [name, value].");
            var preferences = Required<IPreferencesService>(services);
            var value = Preference(preferences, arguments[0], arguments.Length > 1 ? arguments[1] : null);
            Serilog.Log.Information("Debug action: preference {Name} = {Value}", arguments[0], value);
        });
    }

    private static string Preference(IPreferencesService preferences, string name, string? value)
    {
        var p = preferences;
        return name switch
        {
            nameof(IPreferencesService.IsLoggingEnabled) => Flag(() => p.IsLoggingEnabled, v => p.IsLoggingEnabled = v, value),
            nameof(IPreferencesService.MailItemDisplayMode) => Choice(() => p.MailItemDisplayMode, v => p.MailItemDisplayMode = v, value),
            nameof(IPreferencesService.IsShowPreviewEnabled) => Flag(() => p.IsShowPreviewEnabled, v => p.IsShowPreviewEnabled = v, value),
            nameof(IPreferencesService.IsShowSenderPicturesEnabled) => Flag(() => p.IsShowSenderPicturesEnabled, v => p.IsShowSenderPicturesEnabled = v, value),
            nameof(IPreferencesService.IsThreadingEnabled) => Flag(() => p.IsThreadingEnabled, v => p.IsThreadingEnabled = v, value),
            nameof(IPreferencesService.IsMailListGroupHeadersEnabled) => Flag(() => p.IsMailListGroupHeadersEnabled, v => p.IsMailListGroupHeadersEnabled = v, value),
            nameof(IPreferencesService.MailTimeFormatPreference) => Choice(() => p.MailTimeFormatPreference, v => p.MailTimeFormatPreference = v, value),
            nameof(IPreferencesService.EmailSyncIntervalMinutes) => Number(() => p.EmailSyncIntervalMinutes, v => p.EmailSyncIntervalMinutes = v, value),
            nameof(IPreferencesService.MarkAsDelay) => Number(() => p.MarkAsDelay, v => p.MarkAsDelay = v, value),
            nameof(IPreferencesService.MarkAsPreference) => Choice(() => p.MarkAsPreference, v => p.MarkAsPreference = v, value),
            nameof(IPreferencesService.AutoSelectNextItem) => Flag(() => p.AutoSelectNextItem, v => p.AutoSelectNextItem = v, value),
            nameof(IPreferencesService.ReaderFont) => Text(() => p.ReaderFont, v => p.ReaderFont = v, value),
            nameof(IPreferencesService.ReaderFontSize) => Number(() => p.ReaderFontSize, v => p.ReaderFontSize = v, value),
            nameof(IPreferencesService.RenderStyles) => Flag(() => p.RenderStyles, v => p.RenderStyles = v, value),
            nameof(IPreferencesService.RenderPlaintextLinks) => Flag(() => p.RenderPlaintextLinks, v => p.RenderPlaintextLinks = v, value),
            nameof(IPreferencesService.ReaderContentTheme) => Choice(() => p.ReaderContentTheme, v => p.ReaderContentTheme = v, value),
            nameof(IPreferencesService.ReaderDarkModeMethod) => Choice(() => p.ReaderDarkModeMethod, v => p.ReaderDarkModeMethod = v, value),
            nameof(IPreferencesService.ReaderCommandLayout) => p.ReaderCommandLayout ?? string.Empty,
            nameof(IPreferencesService.IsShowActionLabelsEnabled) => Flag(() => p.IsShowActionLabelsEnabled, v => p.IsShowActionLabelsEnabled = v, value),
            nameof(IPreferencesService.ComposerFont) => Text(() => p.ComposerFont, v => p.ComposerFont = v, value),
            nameof(IPreferencesService.ComposerFontSize) => Number(() => p.ComposerFontSize, v => p.ComposerFontSize = v, value),
            nameof(IPreferencesService.IsComposerSpellCheckEnabled) => Flag(() => p.IsComposerSpellCheckEnabled, v => p.IsComposerSpellCheckEnabled = v, value),
            nameof(IPreferencesService.StripLinkTrackingParameters) => Flag(() => p.StripLinkTrackingParameters, v => p.StripLinkTrackingParameters = v, value),
            nameof(IPreferencesService.ConfirmBeforeOpeningLinks) => Flag(() => p.ConfirmBeforeOpeningLinks, v => p.ConfirmBeforeOpeningLinks = v, value),
            nameof(IPreferencesService.AlwaysShowSenderAddress) => Flag(() => p.AlwaysShowSenderAddress, v => p.AlwaysShowSenderAddress = v, value),
            nameof(IPreferencesService.LoadRemoteContentFromContacts) => Flag(() => p.LoadRemoteContentFromContacts, v => p.LoadRemoteContentFromContacts = v, value),
            nameof(IPreferencesService.LoadRemoteContentFromRecipients) => Flag(() => p.LoadRemoteContentFromRecipients, v => p.LoadRemoteContentFromRecipients = v, value),
            nameof(IPreferencesService.WarnBeforeOpeningExecutableAttachments) => Flag(() => p.WarnBeforeOpeningExecutableAttachments, v => p.WarnBeforeOpeningExecutableAttachments = v, value),
            nameof(IPreferencesService.RemoteContentMode) => Choice(() => p.RemoteContentMode, v => p.RemoteContentMode = v, value),
            nameof(IPreferencesService.AreNewMailNotificationsEnabled) => Flag(() => p.AreNewMailNotificationsEnabled, v => p.AreNewMailNotificationsEnabled = v, value),
            nameof(IPreferencesService.AreQuietHoursEnabled) => Flag(() => p.AreQuietHoursEnabled, v => p.AreQuietHoursEnabled = v, value),
            nameof(IPreferencesService.AreCalendarRemindersEnabled) => Flag(() => p.AreCalendarRemindersEnabled, v => p.AreCalendarRemindersEnabled = v, value),
            nameof(IPreferencesService.AreTaskRemindersEnabled) => Flag(() => p.AreTaskRemindersEnabled, v => p.AreTaskRemindersEnabled = v, value),
            nameof(IPreferencesService.NotificationSnoozePreset) => p.NotificationSnoozePreset.ToString(),
            _ => throw new ArgumentException($"debug-settings-pref does not know '{name}'; add it to SettingsDebugActions.Preference."),
        };
    }

    private static string Flag(Func<bool> get, Action<bool> set, string? value)
    {
        if (value is not null) set(bool.Parse(value));
        return get().ToString();
    }

    private static string Number(Func<int> get, Action<int> set, string? value)
    {
        if (value is not null) set(int.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
        return get().ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Text(Func<string?> get, Action<string> set, string? value)
    {
        if (value is not null) set(value);
        return get() ?? string.Empty;
    }

    private static string Choice<T>(Func<T> get, Action<T> set, string? value) where T : struct, Enum
    {
        if (value is not null) set(Enum.Parse<T>(value));
        return get().ToString();
    }

    private static string DescribeState(IServiceProvider services)
    {
        var presenter = services.GetRequiredService<SettingsWindowPresenter>();
        var text = new StringBuilder();
        var window = presenter.Window;
        var page = presenter.CurrentPage;
        var root = page is { } current ? SettingsWindowPresenter.RootPage(current) : (WinoPage?)null;
        text.Append("page=").Append(page?.ToString() ?? "-")
            .Append(" root=").Append(root?.ToString() ?? "-")
            .Append(" back=").Append(presenter.BackDepth)
            .Append(" forward=").Append(presenter.ForwardDepth)
            .Append(" title=").Append(window?.CurrentTitle ?? "-")
            .AppendLine();
        var surface = services.GetService<GtkDialogService>()?.Surface;
        text.Append("window=").Append(window is null ? "none" : window.Window.GetVisible() ? "visible" : "hidden")
            .Append(" active=").Append(window?.Window.IsActive ?? false)
            .Append(" collapsed=").Append(window?.IsCollapsed ?? false)
            .Append(" showsContent=").Append(window?.ShowsContent ?? false)
            .Append(" backButton=").Append(window?.IsBackVisible ?? false)
            .Append(" surface=").Append(surface is null ? "main" : ReferenceEquals(surface, window) ? "settings" : surface.GetType().Name)
            .Append(" view=").Append(presenter.CurrentView?.GetType().Name ?? "-")
            .AppendLine();
        text.Append("description=").Append(window?.CurrentDescription ?? string.Empty).AppendLine();
        if (window is not null)
        {
            text.Append("infobar=").Append(window.InfoBars.Describe()).AppendLine();
            foreach (var entry in window.Sidebar.Entries)
                text.Append("sidebar: ").Append(entry.Title).Append('|').Append(entry.Page?.ToString() ?? "-").AppendLine();
            if (window.CurrentPageWidget is { } widget)
            {
                var rows = new List<string>();
                Walk(widget, 0, rows);
                foreach (var row in rows) text.Append("row: ").Append(row).AppendLine();
            }
        }
        return text.ToString();
    }

    private static void Walk(Gtk.Widget widget, int depth, List<string> lines)
    {
        switch (widget)
        {
            case Adw.PreferencesGroup group:
                lines.Add(Line("group", group.GetTitle(), group.GetDescription(), string.Empty, group, depth));
                break;
            case Adw.SwitchRow row:
                lines.Add(Line("switch", row.GetTitle(), row.GetSubtitle(), row.GetActive().ToString(), row, depth));
                return;
            case Adw.ComboRow row:
                var index = row.GetSelected();
                var selected = (row.GetModel() as Gtk.StringList)?.GetString(index);
                lines.Add(Line("combo", row.GetTitle(), row.GetSubtitle(), $"{index}:{selected}", row, depth));
                return;
            case Adw.SpinRow row:
                lines.Add(Line("spin", row.GetTitle(), row.GetSubtitle(), row.GetValue().ToString(System.Globalization.CultureInfo.InvariantCulture), row, depth));
                return;
            case Adw.EntryRow row:
                lines.Add(Line("entry", row.GetTitle(), string.Empty, row.GetText(), row, depth));
                return;
            case Adw.ExpanderRow row:
                lines.Add(Line("expander", row.GetTitle(), row.GetSubtitle(), row.GetExpanded().ToString(), row, depth));
                foreach (var nested in Descendants(row).OfType<Gtk.ListBox>().Where(list => list.HasCssClass("nested")))
                {
                    for (var child = nested.GetFirstChild(); child is not null; child = child.GetNextSibling()) Walk(child, depth + 1, lines);
                }
                return;
            case Adw.ButtonRow row:
                lines.Add(Line("button", row.GetTitle(), string.Empty, string.Empty, row, depth));
                return;
            case Adw.ActionRow row:
                lines.Add(Line("action", row.GetTitle(), row.GetSubtitle(), SuffixValue(row), row, depth));
                return;
            case Adw.PreferencesRow row:
                lines.Add(Line("custom", row.GetTitle(), string.Empty, string.Empty, row, depth));
                return;
        }
        for (var child = widget.GetFirstChild(); child is not null; child = child.GetNextSibling()) Walk(child, depth, lines);
    }

    private static string Line(string kind, string? title, string? subtitle, string value, Gtk.Widget widget, int depth)
        => $"{kind} | {title} | {subtitle} | {value} | {widget.IsSensitive()} | {widget.GetVisible()} | {depth}";

    private static IEnumerable<Gtk.Widget> Descendants(Gtk.Widget widget)
    {
        for (var child = widget.GetFirstChild(); child is not null; child = child.GetNextSibling())
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static IEnumerable<Gtk.Widget> Suffixes(Adw.PreferencesRow row)
    {
        var box = Descendants(row).FirstOrDefault(widget => widget.HasCssClass("suffixes"));
        return box is null ? [] : Descendants(box);
    }

    private static string SuffixValue(Adw.ActionRow row)
    {
        var values = new List<string>();
        foreach (var widget in Suffixes(row))
        {
            switch (widget)
            {
                case Gtk.Switch toggle: values.Add("switch=" + toggle.GetActive()); break;
                case Gtk.DropDown drop: values.Add("dropdown=" + drop.GetSelected()); break;
                case Gtk.SpinButton spin: values.Add("spin=" + spin.GetValue().ToString(System.Globalization.CultureInfo.InvariantCulture)); break;
                case Gtk.Label label when label.GetVisible() && !string.IsNullOrEmpty(label.GetText()): values.Add(label.GetText()); break;
            }
        }
        return string.Join(", ", values);
    }

    private static string DriveRow(Gtk.Widget page, string titleSpec, string operation, string? value)
    {
        var title = titleSpec;
        var occurrence = 1;
        var hash = titleSpec.LastIndexOf('#');
        if (hash > 0 && int.TryParse(titleSpec[(hash + 1)..], out var parsed))
        {
            title = titleSpec[..hash];
            occurrence = parsed;
        }

        var row = Descendants(page).OfType<Adw.PreferencesRow>()
            // An expander row draws its title with an inner action row of the same title (CSS class "header"); skip that one.
            .Where(candidate => candidate.IsVisible() && !candidate.HasCssClass("header") && string.Equals(candidate.GetTitle(), title, StringComparison.Ordinal))
            .Skip(occurrence - 1).FirstOrDefault() ?? throw new InvalidOperationException($"No visible row is titled '{titleSpec}'.");
        if (!row.IsSensitive()) throw new InvalidOperationException($"The row '{titleSpec}' is insensitive.");

        switch (operation)
        {
            case "toggle":
                if (row is Adw.SwitchRow switchRow)
                {
                    switchRow.SetActive(!switchRow.GetActive());
                    return switchRow.GetActive().ToString();
                }
                var toggle = Suffix<Gtk.Switch>(row);
                toggle.SetActive(!toggle.GetActive());
                return toggle.GetActive().ToString();
            case "select":
                var index = uint.Parse(Require(value, operation));
                if (row is Adw.ComboRow combo)
                {
                    combo.SetSelected(index);
                    return combo.GetSelected().ToString();
                }
                var drop = Suffix<Gtk.DropDown>(row);
                drop.SetSelected(index);
                return drop.GetSelected().ToString();
            case "value":
                var number = double.Parse(Require(value, operation), System.Globalization.CultureInfo.InvariantCulture);
                if (row is Adw.SpinRow spinRow)
                {
                    spinRow.SetValue(number);
                    return spinRow.GetValue().ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                var spin = Suffix<Gtk.SpinButton>(row);
                spin.SetValue(number);
                return spin.GetValue().ToString(System.Globalization.CultureInfo.InvariantCulture);
            case "text":
                var text = value ?? string.Empty;
                if (row is Adw.EntryRow entryRow)
                {
                    entryRow.SetText(text);
                    return entryRow.GetText();
                }
                var entry = Suffix<Gtk.Entry>(row);
                entry.SetText(text);
                return entry.GetText();
            case "activate":
                if (!string.IsNullOrEmpty(value))
                {
                    var button = Suffixes(row).OfType<Gtk.Button>().FirstOrDefault(candidate => candidate.GetLabel() == value || candidate.GetTooltipText() == value)
                        ?? throw new InvalidOperationException($"The row '{titleSpec}' has no button '{value}'.");
                    if (!button.IsSensitive()) throw new InvalidOperationException($"The button '{value}' is insensitive.");
                    button.Activate();
                    return "button " + value;
                }
                if (!row.GetActivatable()) throw new InvalidOperationException($"The row '{titleSpec}' is not activatable.");
                row.Activate();
                return "activated";
            case "expand":
                if (row is not Adw.ExpanderRow expander) throw new InvalidOperationException($"The row '{titleSpec}' is not an expander.");
                expander.SetExpanded(value == "true");
                return expander.GetExpanded().ToString();
            default:
                throw new ArgumentException($"Unknown operation '{operation}'.");
        }
    }

    private static string Require(string? value, string operation)
        => string.IsNullOrEmpty(value) ? throw new ArgumentException($"{operation} needs a value.") : value;

    private static T Suffix<T>(Adw.PreferencesRow row) where T : Gtk.Widget
    {
        var widget = Suffixes(row).OfType<T>().FirstOrDefault() ?? throw new InvalidOperationException($"The row '{row.GetTitle()}' has no {typeof(T).Name}.");
        if (!widget.IsSensitive()) throw new InvalidOperationException($"The {typeof(T).Name} of '{row.GetTitle()}' is insensitive.");
        return widget;
    }

    private static SettingsWindowPresenter Presenter(Func<IServiceProvider?> services) => Required<SettingsWindowPresenter>(services);

    private static SettingsWindow Window(Func<IServiceProvider?> services)
        => Presenter(services).Window ?? throw new InvalidOperationException("The Settings window has not been opened.");

    private static T Required<T>(Func<IServiceProvider?> services) where T : notnull
        => (services() ?? throw new InvalidOperationException("The app has not started.")).GetRequiredService<T>();

    private static async void Observe(string name, Task task)
    {
        try
        {
            await task;
            Serilog.Log.Information("Debug action {Name} done.", name);
        }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action {Name} failed.", name); }
    }

    private static void Add(Adw.Application application, string name, string parameterType, Action<GLib.Variant> handler)
    {
        var action = Gio.SimpleAction.New(name, GLib.VariantType.New(parameterType));
        action.OnActivate += (_, args) => Run(name, () =>
        {
            if (args.Parameter is not { } parameter) throw new ArgumentException($"{name} needs a parameter of type {parameterType}.");
            handler(parameter);
        });
        application.AddAction(action);
    }

    private static void Add(Adw.Application application, string name, Action handler)
    {
        var action = Gio.SimpleAction.New(name, null);
        action.OnActivate += (_, _) => Run(name, handler);
        application.AddAction(action);
    }

    private static void Run(string name, Action handler)
    {
        try { handler(); }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action {Name} failed.", name); }
    }
}
#endif
