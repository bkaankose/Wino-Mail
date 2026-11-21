#if DEBUG
using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// Debug actions of the privacy pages. <c>debug-remote-rules-seed</c> <c>s</c> (a count) adds synthetic sender rules
/// ending in <c>.test</c>, every tenth a domain, with rotating policies; <c>debug-remote-rules-clear</c> removes them;
/// <c>debug-browsers</c> <c>s</c> (a path) writes the installed browsers and the all-accounts choice.
/// </summary>
internal static class PrivacyDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        Add(application, "debug-remote-rules-seed", parameter =>
        {
            var count = int.Parse(parameter, CultureInfo.InvariantCulture);
            Observe("debug-remote-rules-seed", SeedAsync(Required<IRemoteContentRuleStore>(services), count));
        });
        Add(application, "debug-remote-rules-clear", _ => Observe("debug-remote-rules-clear", ClearAsync(Required<IRemoteContentRuleStore>(services))));
        Add(application, "debug-browsers", path => Observe("debug-browsers", BrowsersAsync(services, path)));
    }

    private static async Task SeedAsync(IRemoteContentRuleStore store, int count)
    {
        for (var index = 0; index < count; index++)
        {
            var domain = index % 10 == 0;
            await store.UpsertAsync(new RemoteContentSenderRule
            {
                Pattern = domain ? $"example{index}.test" : $"sender{index}@example{index % 7}.test",
                Kind = domain ? RemoteContentRuleKind.Domain : RemoteContentRuleKind.Address,
                Policy = (RemoteContentSenderPolicy)(index % 3),
                Source = RemoteContentRuleSource.Manual,
                CreatedUtc = DateTime.UtcNow,
            });
        }
        Serilog.Log.Information("Debug action: seeded {Count} sender rules.", count);
    }

    private static async Task ClearAsync(IRemoteContentRuleStore store)
    {
        var removed = 0;
        foreach (var rule in await store.GetAllAsync())
        {
            if (!rule.Pattern.EndsWith(".test", StringComparison.Ordinal)) continue;
            await store.DeleteAsync(rule.Id);
            removed++;
        }
        Serilog.Log.Information("Debug action: removed {Count} sender rules.", removed);
    }

    private static async Task BrowsersAsync(Func<IServiceProvider?> services, string path)
    {
        var catalog = await Required<IInstalledBrowserService>(services).GetBrowsersAsync();
        var text = new StringBuilder();
        text.Append("complete=").Append(catalog.IsComplete).AppendLine();
        foreach (var browser in catalog.Browsers) text.Append(browser.BrowserId).Append(" | ").Append(browser.DisplayName).AppendLine();
        var current = Required<ILinkBrowserService>(services).AllAccountsBrowser;
        text.Append("allAccounts=").Append(current?.BrowserId ?? "system default").AppendLine();
        DebugActions.WriteText(path, text.ToString());
        Serilog.Log.Information("Debug action: browsers written to {Path}.", path);
    }

    private static T Required<T>(Func<IServiceProvider?> services) where T : notnull
        => (services() ?? throw new InvalidOperationException("The app has not started.")).GetRequiredService<T>();

    private static async void Observe(string name, Task task)
    {
        try { await task; }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action {Name} failed.", name); }
    }

    private static void Add(Adw.Application application, string name, Action<string> handler)
    {
        var action = Gio.SimpleAction.New(name, GLib.VariantType.New("s"));
        action.OnActivate += (sender, args) =>
        {
            try { handler(args.Parameter?.GetString(out _) ?? string.Empty); }
            catch (Exception exception) { Serilog.Log.Error(exception, "Debug action {Name} failed.", name); }
        };
        application.AddAction(action);
    }
}
#endif
