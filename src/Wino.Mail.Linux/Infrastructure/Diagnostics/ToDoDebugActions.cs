#if DEBUG
using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Mail.Linux.Views.Shell;
using Wino.Mail.Linux.Views.ToDo;
using Wino.Mail.ViewModels.MenuItems;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-todo</c> <c>as</c>: <c>[verb, args…]</c>. Each verb logs one line <c>Debug action debug-todo &lt;verb&gt;:
/// &lt;result&gt;</c>; when the last argument ends in <c>.txt</c> the result is also written there. Page verbs: state,
/// lists, view MyDay|Important|Planned|All|Completed, list index|title, options (opens the list options menu and
/// describes it), options-run label, sidebar-menu row, sidebar-run row entry [sub entry], new-list, new-group. Later
/// packages add theirs through the partial <c>Add…Verbs</c> methods.
/// </summary>
internal static partial class ToDoDebugActions
{
    private static readonly Dictionary<string, Func<string[], Task<string>>> Verbs = new(StringComparer.OrdinalIgnoreCase);
    private static Func<IServiceProvider?> _services = static () => null;

    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        _services = services;
        AddPageVerbs();
        AddListVerbs();
        AddFilterVerbs();
        AddDrawerVerbs();
        AddReorderVerbs();
        AddTransferVerbs();
        AddFieldVerbs();
        AddFlaggedVerbs();
        var action = Gio.SimpleAction.New("debug-todo", GLib.VariantType.New("as"));
        action.OnActivate += (_, args) => Run(args.Parameter?.GetStrv(out var _) ?? []);
        application.AddAction(action);
    }

    static partial void AddListVerbs();
    static partial void AddFilterVerbs();
    static partial void AddDrawerVerbs();
    static partial void AddReorderVerbs();
    static partial void AddTransferVerbs();
    static partial void AddFieldVerbs();
    static partial void AddFlaggedVerbs();

    private static void Verb(string name, Func<string[], Task<string>> run) => Verbs[name] = run;

    private static void Verb(string name, Func<string[], string> run) => Verbs[name] = args => Task.FromResult(run(args));

    private static IServiceProvider Services => _services() ?? throw new InvalidOperationException("no services");

    internal static ToDoPageView Page
        => ToDoPageView.DebugCurrent is { } reference && reference.TryGetTarget(out var page) ? page : throw new InvalidOperationException("no page");

    private static ShellView Shell
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
            if (result != "no page") Serilog.Log.Error(exception, "Debug action debug-todo {Verb} failed.", verb);
        }
        Serilog.Log.Information("Debug action debug-todo {Verb}: {Result}", verb, result);
        if (path is null) return;
        try { DebugActions.WriteText(path, result); }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-todo result could not be written."); }
    }

    private static void AddPageVerbs()
    {
        Verb("state", _ => State(Page));
        Verb("lists", _ =>
        {
            var vm = Page.ViewModel;
            var text = new StringBuilder();
            for (var index = 0; index < vm.TaskLists.Count; index++)
            {
                if (vm.TaskLists[index] is not { } list) continue;
                text.Append(index).Append(':').Append(list.Title).Append(" account=").Append(list.MailAccountId)
                    .Append(" group=").Append(list.GroupId).Append(" readOnly=").Append(list.IsReadOnly)
                    .Append(" canDelete=").Append(list.CanDelete).Append(" source=").Append(list.SourceKind).Append(" | ");
            }
            return text.ToString();
        });
        Verb("view", async args =>
        {
            var page = Page;
            var view = Enum.Parse<TaskViewKind>(args.Length > 0 ? args[0] : string.Empty, true);
            await page.ViewModel.SelectPresentationSurfaceAsync(view, null);
            return State(page);
        });
        Verb("list", async args =>
        {
            var page = Page;
            var vm = page.ViewModel;
            var key = string.Join(' ', args);
            var list = int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < vm.TaskLists.Count
                ? vm.TaskLists[index]
                : vm.TaskLists.FirstOrDefault(candidate => string.Equals(candidate?.Title, key, StringComparison.Ordinal));
            if (list is null) return "no list " + key;
            await vm.SelectPresentationSurfaceAsync(TaskViewKind.All, list);
            return State(page);
        });
        Verb("options", _ =>
        {
            var page = Page;
            if (!page.Header.IsOptionsVisible) return "options hidden";
            var popover = ContextMenu.Show(page.Header.Root, page.BuildListOptions().Build());
            return popover.Describe();
        });
        Verb("options-run", args =>
        {
            var page = Page;
            var label = string.Join(' ', args);
            var popover = ContextMenu.Current is { IsOpen: true } open ? open : ContextMenu.Show(page.Header.Root, page.BuildListOptions().Build());
            return popover.Activate(label) ? "ran " + label : "no entry " + label;
        });
        Verb("sidebar-menu", args =>
        {
            var shell = Shell;
            var entries = ToDoSidebarMenus.Build(SidebarDebugActions.Resolve(shell.Sidebar, string.Join(' ', args)), shell.SidebarMenuContext);
            return string.Join("; ", entries.Select(static entry => entry.IsSeparator ? "—"
                : $"{entry.Title} enabled={entry.IsEnabled} destructive={entry.IsDestructive} glyph={entry.Glyph}"
                    + (entry.Children is { Length: > 0 } children ? " [" + string.Join(", ", children.Select(static child => child.Title)) + "]" : string.Empty)));
        });
        Verb("sidebar-run", async args =>
        {
            if (args.Length < 2) throw new ArgumentException("sidebar-run needs a row and an entry");
            var shell = Shell;
            var entries = ToDoSidebarMenus.Build(SidebarDebugActions.Resolve(shell.Sidebar, args[0]), shell.SidebarMenuContext);
            var entry = entries.FirstOrDefault(entry => string.Equals(entry.Title, args[1], StringComparison.Ordinal)) ?? throw new ArgumentException("no entry");
            if (args.Length > 2)
                entry = entry.Children?.FirstOrDefault(child => string.Equals(child.Title, args[2], StringComparison.Ordinal)) ?? throw new ArgumentException("no sub entry");
            if (entry.Run is null || !entry.IsEnabled) return "disabled";
            await entry.Run();
            return "ran";
        });
        Verb("new-list", async _ =>
        {
            var sidebar = Shell.Sidebar;
            var row = sidebar.Rows.OfType<NewTaskListMenuItem>().FirstOrDefault() ?? throw new ArgumentException("no New list row");
            await sidebar.InvokeAsync(row);
            return "invoked";
        });
        Verb("new-group", async _ =>
        {
            var sidebar = Shell.Sidebar;
            var row = sidebar.Rows.OfType<NewTaskListMenuItem>().FirstOrDefault() ?? throw new ArgumentException("no New list row");
            if (row.NewGroupRequested is not { } newGroup) return "no new group action";
            await newGroup();
            return "ran";
        });
    }

    internal static string State(ToDoPageView page)
    {
        var vm = page.ViewModel;
        var text = new StringBuilder();
        text.Append("groups=").Append(vm.TaskGroups.Count).Append(" tasks=").Append(vm.TaskGroups.Sum(static group => group.Count))
            .Append(" lists=").Append(vm.TaskLists.Count).Append(" accounts=").Append(vm.Accounts.Count)
            .Append(" ready=").Append(vm.Readiness.IsReady).Append(" blocked=").Append(page.IsBlocked)
            .Append(" loading=").Append(vm.IsLoading).Append(" empty=").Append(vm.IsEmpty).Append(" emptyShown=").Append(page.IsEmptyStateShown)
            .Append(" emptyGlyph=").Append(page.EmptyStateGlyphName)
            .Append(" compact=").Append(vm.IsCompactLayout).Append(" layout=").Append(page.Layout.State).Append(" showsEnd=").Append(page.Layout.ShowsEnd)
            .Append(" selected=").Append(vm.SelectedTasks.Count).Append(" view=").Append(vm.SelectedView).Append(" list=").Append(vm.SelectedList?.Title)
            .Append(" sort=").Append(vm.SelectedSort).Append(" title=").Append(page.Header.Title)
            .Append(" subtitle=").Append(vm.HasSurfaceSubtitle ? vm.SelectedSurfaceSubtitle : string.Empty)
            .Append(" rename=").Append(page.Header.IsRenameVisible).Append(" options=").Append(page.Header.IsOptionsVisible)
            .Append(" suggestions=").Append(page.Header.IsSuggestionsVisible)
            .Append(" composerExpanded=").Append(vm.IsComposerExpanded)
            .Append(" pageWidth=").Append(page.Root.GetWidth());
        return text.ToString();
    }
}
#endif
