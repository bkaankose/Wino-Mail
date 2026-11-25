#if DEBUG
using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.Linux.Views.Contacts;
using Wino.Mail.Linux.Views.Shell;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Lists;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-contacts</c> <c>as</c>: <c>[verb, args…]</c>. Each verb logs one line <c>Debug action debug-contacts
/// &lt;verb&gt;: &lt;result&gt;</c>; when the last argument ends in <c>.txt</c> the result is also written there. Verbs of
/// the list: state, names, select i, select-many i j…, clear, search text, submit text, filter title, favorite i,
/// load-more, scroll top|end|px, sidebar-menu title, sidebar-run title entry. Later packages add theirs through the
/// partial <c>Add…Verbs</c> methods.
/// </summary>
internal static partial class ContactsDebugActions
{
    private static readonly Dictionary<string, Func<string[], Task<string>>> Verbs = new(StringComparer.OrdinalIgnoreCase);
    private static Func<IServiceProvider?> _services = static () => null;

    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        _services = services;
        AddListVerbs();
        AddDetailVerbs();
        AddEditVerbs();
        AddDragVerbs();
        var action = Gio.SimpleAction.New("debug-contacts", GLib.VariantType.New("as"));
        action.OnActivate += (_, args) => Run(args.Parameter?.GetStrv(out var _) ?? []);
        application.AddAction(action);
    }

    static partial void AddDetailVerbs();
    static partial void AddEditVerbs();
    static partial void AddDragVerbs();

    private static void Verb(string name, Func<string[], Task<string>> run) => Verbs[name] = run;

    private static void Verb(string name, Func<string[], string> run) => Verbs[name] = args => Task.FromResult(run(args));

    private static IServiceProvider Services => _services() ?? throw new InvalidOperationException("no services");

    internal static ContactsPageView Page
        => ContactsPageView.DebugCurrent is { } reference && reference.TryGetTarget(out var page) ? page : throw new InvalidOperationException("no page");

    internal static ShellView Shell
        => Services.GetService<GtkNavigationService>()?.Shell ?? throw new InvalidOperationException("The shell is not shown.");

    private static async void Run(string[] arguments)
    {
        var verb = arguments.Length > 0 ? arguments[0] : string.Empty;
        string? path = arguments.Length > 1 && arguments[^1].EndsWith(".txt", StringComparison.Ordinal) ? arguments[^1] : null;
        var rest = arguments.Length > 1 ? arguments[1..(path is null ? arguments.Length : arguments.Length - 1)] : [];
        string result;
        try
        {
            result = Verbs.TryGetValue(verb, out var run)
                ? await run(rest)
                : "verbs: " + string.Join(", ", Verbs.Keys.Order(StringComparer.Ordinal));
        }
        catch (Exception exception)
        {
            result = exception.Message == "no page" ? "no page" : "error: " + exception.Message;
            if (result != "no page") Serilog.Log.Error(exception, "Debug action debug-contacts {Verb} failed.", verb);
        }
        Serilog.Log.Information("Debug action debug-contacts {Verb}: {Result}", verb, result);
        if (path is null) return;
        try { DebugActions.WriteText(path, result); }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-contacts result could not be written."); }
    }

    private static void AddListVerbs()
    {
        Verb("state", _ => State(Page));
        Verb("names", _ => Names(Page));
        Verb("select", args =>
        {
            var page = Page;
            ListViews.Select(page.Selection, [Index(args, 0)]);
            return Selected(page);
        });
        Verb("select-many", args =>
        {
            var page = Page;
            ListViews.Select(page.Selection, [.. args.Select(static value => uint.Parse(value, CultureInfo.InvariantCulture))]);
            return Selected(page);
        });
        Verb("clear", _ =>
        {
            var page = Page;
            page.Selection.UnselectAll();
            return Selected(page);
        });
        Verb("search", async args =>
        {
            var page = Page;
            await page.SearchTextChangedAsync(string.Join(' ', args));
            return $"rows={page.RowCount} sections={page.SectionCount} search={page.IsShowingSearchResults}";
        });
        Verb("submit", async args =>
        {
            var page = Page;
            await page.SearchSubmittedAsync(string.Join(' ', args));
            return $"rows={page.RowCount} {Selected(page)}";
        });
        Verb("filter", async args =>
        {
            var sidebar = Shell.Sidebar;
            await sidebar.InvokeAsync(SidebarDebugActions.Resolve(sidebar, string.Join(' ', args)));
            return "invoked";
        });
        Verb("favorite", args =>
        {
            var page = Page;
            var item = page.ItemAtPosition(Index(args, 0)) ?? throw new ArgumentException("no row");
            if (page.Cells.CellOf(item) is { } cell) cell.Row.FavoriteButton.Activate();
            else throw new ArgumentException("row not bound");
            return $"{item.Name} was favorite={item.IsFavorite}";
        });
        Verb("load-more", _ =>
        {
            var page = Page;
            var command = page.ViewModel.LoadMoreContactsCommand;
            if (!command.CanExecute(null)) return "cannot";
            command.ExecuteAsync(null).ContinueWith(static task => Serilog.Log.Error(task.Exception, "Debug load more failed."), TaskContinuationOptions.OnlyOnFaulted);
            return "started";
        });
        Verb("scroll", args =>
        {
            var page = Page;
            if (page.List.GetParent() is not Gtk.ScrolledWindow scroller) return "no scroller";
            var adjustment = scroller.GetVadjustment();
            var target = args.Length == 0 ? "end" : args[0];
            var value = target switch
            {
                "top" => adjustment.GetLower(),
                "end" => adjustment.GetUpper() - adjustment.GetPageSize(),
                _ => adjustment.GetValue() + double.Parse(target, CultureInfo.InvariantCulture),
            };
            adjustment.SetValue(value);
            return $"value={adjustment.GetValue():0} upper={adjustment.GetUpper():0} page={adjustment.GetPageSize():0}";
        });
        Verb("sync-full", async _ =>
        {
            var accounts = await Services.GetRequiredService<Wino.Core.Domain.Interfaces.IAccountService>().GetAccountsAsync();
            var manager = Services.GetRequiredService<Wino.Services.Interfaces.ISynchronizationManager>();
            var results = new List<string>();
            foreach (var account in accounts.Where(static a => a.IsContactAccessEnabled))
            {
                var result = await manager.SynchronizeContactsAsync(new Wino.Core.Domain.Models.Synchronization.ContactSynchronizationOptions
                {
                    AccountId = account.Id,
                    Type = Wino.Core.Domain.Enums.ContactSynchronizationType.Full,
                });
                results.Add(account.Address + "=" + result.CompletedState);
            }
            return string.Join(", ", results);
        });
        Verb("sidebar-menu", args =>
        {
            var shell = Shell;
            var entries = ContactsSidebarMenus.Build(SidebarDebugActions.Resolve(shell.Sidebar, string.Join(' ', args)), shell.SidebarMenuContext);
            return string.Join("; ", entries.Select(static entry => $"{entry.Title} enabled={entry.IsEnabled} destructive={entry.IsDestructive}"));
        });
        Verb("sidebar-run", async args =>
        {
            if (args.Length < 2) throw new ArgumentException("sidebar-run needs a row and an entry");
            var shell = Shell;
            var entries = ContactsSidebarMenus.Build(SidebarDebugActions.Resolve(shell.Sidebar, args[0]), shell.SidebarMenuContext);
            var entry = entries.FirstOrDefault(entry => string.Equals(entry.Title, args[1], StringComparison.Ordinal)) ?? throw new ArgumentException("no entry");
            if (entry.Run is null || !entry.IsEnabled) return "disabled";
            await entry.Run();
            return "ran";
        });
    }

    private static uint Index(string[] args, int position)
        => args.Length > position ? uint.Parse(args[position], CultureInfo.InvariantCulture) : throw new ArgumentException("missing index");

    private static string Selected(ContactsPageView page)
        => $"selected={page.ViewModel.SelectedContacts.Count} gtk={ListViews.SelectedPositions(page.Selection).Length} current={page.ViewModel.SelectedContact?.Name}";

    private static string State(ContactsPageView page)
    {
        var vm = page.ViewModel;
        var text = new StringBuilder();
        text.Append("contacts=").Append(vm.Contacts.Count).Append(" total=").Append(vm.TotalContactsCount)
            .Append(" rows=").Append(page.RowCount).Append(" sections=").Append(page.SectionCount)
            .Append(" loading=").Append(vm.IsLoading).Append(" loadingMore=").Append(vm.IsLoadingMore).Append(" more=").Append(vm.HasMoreContacts)
            .Append(" blocked=").Append(page.IsBlocked).Append(" readiness=").Append(vm.Readiness.State)
            .Append(" layout=").Append(page.Layout.State).Append(" showsEnd=").Append(page.Layout.ShowsEnd)
            .Append(' ').Append(Selected(page))
            .Append(" filter=").Append(vm.SelectedFilter?.Name)
            .Append(" search=").Append(page.IsShowingSearchResults)
            .Append(" cells=").Append(page.Cells.CellCount).Append(" bound=").Append(page.Cells.BoundCount)
            .Append(" firstRowsMs=").Append(page.FirstRowsMilliseconds.ToString("0", CultureInfo.InvariantCulture))
            .Append(" listWidth=").Append(page.List.GetWidth()).Append(" pageWidth=").Append(page.Root.GetWidth());
        return text.ToString();
    }

    private static string Names(ContactsPageView page)
    {
        var text = new StringBuilder();
        var sections = page.Sections;
        for (uint position = 0; position < page.RowCount; position++)
        {
            if (sections.ItemAt(position) is not { } item) continue;
            text.Append(position).Append(':').Append(item.InitialLetter).Append(':').Append(item.Name)
                .Append(item.IsFavorite ? "*" : string.Empty).Append(" | ");
        }
        return text.ToString();
    }
}
#endif
