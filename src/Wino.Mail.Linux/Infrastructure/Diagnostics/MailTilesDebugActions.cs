#if DEBUG
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Linux.Views.Mail;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-mail-tiles</c> <c>as</c> on <see cref="MailListView.DebugCurrent"/>: <c>['state', path]</c> writes
/// <c>position|subject|compact|count|texts|height</c> per bound row; <c>['seed', name, '#bg', '#fg']</c> creates a category for the
/// first account; <c>['assign', row, name]</c> / <c>['unassign', row, name]</c> toggle it through the ViewModel;
/// <c>['unseed', name]</c> deletes it.
/// </summary>
internal static class MailTilesDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-mail-tiles", GLib.VariantType.New("as"));
        action.OnActivate += (_, args) => Run(args.Parameter?.GetStrv(out var _) ?? [], services);
        application.AddAction(action);
    }

    private static async void Run(string[] arguments, Func<IServiceProvider?> services)
    {
        try
        {
            var command = Argument(arguments, 0);
            var provider = services() ?? throw new InvalidOperationException("No services.");
            switch (command)
            {
                case "state":
                {
                    var view = MailListView.DebugCurrent ?? throw new InvalidOperationException("No mail list is shown.");
                    var rows = new List<(uint Position, string Line)>();
                    view.Cells.ForEachBound((cell, entry) =>
                    {
                        if (view.PositionOf(entry) is not { } position) return;
                        var subject = entry.Row.SourceItem is Wino.Mail.ViewModels.Data.MailItemViewModel item ? item.Subject : string.Empty;
                        var tiles = cell.Row.Tiles?.Describe() ?? "none";
                        rows.Add((position, $"{position}|{subject}|{tiles}|{cell.Root.GetHeight()}"));
                    });
                    var text = new StringBuilder();
                    foreach (var row in rows.OrderBy(static row => row.Position)) text.Append(row.Line).Append('\n');
                    DebugActions.WriteText(Argument(arguments, 1), text.ToString());
                    Serilog.Log.Information("Debug action: mail tiles state rows={Rows}", rows.Count);
                    break;
                }
                case "seed":
                {
                    var account = await FirstAccountAsync(provider);
                    var categories = provider.GetRequiredService<IMailCategoryService>();
                    var name = Argument(arguments, 1);
                    if (await categories.CategoryNameExistsAsync(account, name))
                    {
                        Serilog.Log.Information("Debug action: mail tiles seed {Name} exists", name);
                        break;
                    }
                    await categories.CreateCategoryAsync(new MailCategory
                    {
                        Id = Guid.NewGuid(),
                        MailAccountId = account,
                        Name = name,
                        BackgroundColorHex = Argument(arguments, 2),
                        TextColorHex = Argument(arguments, 3),
                        Source = Wino.Core.Domain.Enums.MailCategorySource.Local,
                    });
                    Serilog.Log.Information("Debug action: mail tiles seed {Name}", name);
                    break;
                }
                case "assign":
                case "unassign":
                {
                    var view = MailListView.DebugCurrent ?? throw new InvalidOperationException("No mail list is shown.");
                    var row = view.DebugFindRow(Argument(arguments, 1)) ?? throw new ArgumentException($"No row '{arguments[1]}'.");
                    var category = await FindAsync(provider, Argument(arguments, 2));
                    var items = MailListTargets.For(row, view.ViewModel).Items;
                    await view.ViewModel.ToggleCategoryAssignmentAsync(category, items, command == "unassign");
                    Serilog.Log.Information("Debug action: mail tiles {Command} {Row} {Name} targets={Targets}", command, arguments[1], category.Name, items.Length);
                    break;
                }
                case "unseed":
                {
                    var category = await FindAsync(provider, Argument(arguments, 1));
                    await provider.GetRequiredService<IMailCategoryService>().DeleteCategoryAsync(category.Id);
                    Serilog.Log.Information("Debug action: mail tiles unseed {Name}", category.Name);
                    break;
                }
                default: throw new ArgumentException($"'{command}' is not a mail tiles command.");
            }
        }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-mail-tiles failed."); }
    }

    private static async Task<Guid> FirstAccountAsync(IServiceProvider services)
    {
        var accounts = await services.GetRequiredService<IAccountService>().GetAccountsAsync();
        return accounts.FirstOrDefault()?.Id ?? throw new InvalidOperationException("No account.");
    }

    private static async Task<MailCategory> FindAsync(IServiceProvider services, string name)
    {
        var account = await FirstAccountAsync(services);
        var categories = await services.GetRequiredService<IMailCategoryService>().GetCategoriesAsync(account);
        return categories.FirstOrDefault(category => string.Equals(category.Name, name, StringComparison.Ordinal))
            ?? throw new ArgumentException($"No category '{name}'.");
    }

    private static string Argument(string[] arguments, int index)
        => index < arguments.Length ? arguments[index] : throw new ArgumentException($"Argument {index} is missing.");
}
#endif
