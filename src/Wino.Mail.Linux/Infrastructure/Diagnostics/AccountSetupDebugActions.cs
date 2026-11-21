#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Linux.Views.Onboarding;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// The address-first setup page: <c>debug-setup-open 'window'|'settings'</c> opens it with the wizard or the Settings
/// context; <c>debug-setup-state 'path'</c> writes the step, the visible parts and the counts (no password);
/// <c>debug-setup-type "['email'|'name'|field, text]"</c> types into an entry widget (values are not logged; see <see cref="LabValue"/>);
/// <c>debug-setup-choose 'i'</c> picks a sign-in method card; <c>debug-setup-provider 'Kind'</c> runs the provider
/// picker's choice; <c>debug-setup-picker ''</c> opens the picker; <c>debug-setup-primary ''</c> and
/// <c>debug-setup-back ''</c> press the footer buttons.
/// </summary>
internal static class AccountSetupDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        Add(application, "debug-setup-open", "s", value => Observe(OpenAsync(services, value.GetString(out _))));
        Add(application, "debug-setup-state", "s", value => Write(value.GetString(out _)));
        Add(application, "debug-setup-type", "as", value =>
        {
            var args = value.GetStrv(out _);
            if (args.Length < 2) return;
            var ok = Current().DebugType(args[0], LabValue(args[1]));
            Serilog.Log.Information("Debug action: setup field {Field} typed={Ok}.", args[0], ok);
        });
        Add(application, "debug-setup-choose", "s", value =>
        {
            var vm = Current().DebugViewModel;
            if (int.TryParse(value.GetString(out _), out var index) && index >= 0 && index < vm.StepChoices.Count) vm.SelectedChoice = vm.StepChoices[index];
        });
        Add(application, "debug-setup-provider", "s", value =>
        {
            var vm = Current().DebugViewModel;
            var name = value.GetString(out _);
            var choice = vm.ProviderChoices.FirstOrDefault(item => string.Equals(item.Kind.ToString(), name, StringComparison.OrdinalIgnoreCase));
            if (choice is null) Serilog.Log.Warning("Debug action: no provider {Kind}.", name);
            else Observe(vm.ChangeProviderCommand.ExecuteAsync(choice));
        });
        Add(application, "debug-setup-picker", "s", _ => Current().DebugShowProviderPicker());
        Add(application, "debug-setup-primary", "s", _ =>
        {
            var vm = Current().DebugViewModel;
            if (vm.PrimaryCommand.CanExecute(null)) Observe(vm.PrimaryCommand.ExecuteAsync(null));
            else Serilog.Log.Information("Debug action: setup primary is disabled.");
        });
        Add(application, "debug-setup-back", "s", _ =>
        {
            var vm = Current().DebugViewModel;
            if (vm.BackCommand.CanExecute(null)) Observe(vm.BackCommand.ExecuteAsync(null));
        });
    }

    /// <summary>
    /// <c>@lab:&lt;lease or manifest path&gt;|&lt;account key&gt;|&lt;field&gt;</c> reads a value of that lab account (the shared
    /// password when the account has none) so a secret never travels on a command line; anything else is the text itself.
    /// </summary>
    internal static string LabValue(string text)
    {
        if (!text.StartsWith("@lab:", StringComparison.Ordinal)) return text;
        var parts = text[5..].Split('|');
        if (parts.Length != 3) throw new ArgumentException("Expected @lab:<path>|<key>|<field>.");
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(parts[0]));
        var root = document.RootElement;
        foreach (var list in new[] { "accounts", "discoveryAccounts", "pop3Accounts", "oauthAccounts", "extraAccounts" })
        {
            if (!root.TryGetProperty(list, out var items) || items.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
            foreach (var account in items.EnumerateArray())
            {
                if (!account.TryGetProperty("key", out var key) || key.GetString() != parts[1]) continue;
                if (account.TryGetProperty(parts[2], out var value)) return value.ToString();
                if (parts[2] == "password" && root.TryGetProperty("password", out var shared)) return shared.ToString();
                return string.Empty;
            }
        }
        throw new InvalidOperationException($"No lab account {parts[1]}.");
    }

    private static AccountSetupView Current() => AccountSetupView.DebugCurrent ?? throw new InvalidOperationException("The setup page is not open.");

    private static void Write(string path)
    {
        var view = Current();
        DebugActions.WriteText(path, view.DebugDescribe() + $"focus={view.DebugFocus?.GetType().Name}\n");
    }

    private static async Task OpenAsync(Func<IServiceProvider?> services, string host)
    {
        var provider = services() ?? throw new InvalidOperationException("The app has not started.");
        var context = host == "settings" ? ProviderSelectionNavigationContext.CreateForSettingsAddAccount() : ProviderSelectionNavigationContext.CreateForWizard();
        await provider.GetRequiredService<IDispatcher>().ExecuteOnUIThread(() =>
            provider.GetRequiredService<INavigationService>().Navigate(WinoPage.AccountSetupPage, context));
        Serilog.Log.Information("Debug action: setup page opened for {Host}.", host);
    }

    private static void Add(Adw.Application application, string name, string type, Action<GLib.Variant> run)
    {
        var action = Gio.SimpleAction.New(name, GLib.VariantType.New(type));
        action.OnActivate += (_, args) =>
        {
            if (args.Parameter is not { } parameter) return;
            try { run(parameter); }
            catch (Exception exception) { Serilog.Log.Error(exception, "Debug action {Action} failed.", name); }
        };
        application.AddAction(action);
    }

    private static async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { Serilog.Log.Error(exception, "Setup debug action failed."); }
    }
}
#endif
