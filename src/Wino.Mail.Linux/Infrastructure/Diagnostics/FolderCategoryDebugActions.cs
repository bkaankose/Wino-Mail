#if DEBUG
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Linux.Views.Settings;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-folder-customization</c> (<c>as</c>): <c>['open', address]</c>, <c>['move', folder, 'up'|'down']</c> (the
/// drag list's Move path), <c>['page', path]</c> (the open page's sections), <c>['state', address, path]</c> (folders read
/// fresh from the service). <c>debug-categories</c> (<c>as</c>): <c>['open', address]</c>,
/// <c>['seed', address, name, bg, fg]</c>, <c>['list', address, path]</c>, <c>['page', path]</c>,
/// <c>['clear', address]</c> (names starting with "Lab ").
/// </summary>
internal static class FolderCategoryDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var folders = Gio.SimpleAction.New("debug-folder-customization", GLib.VariantType.New("as"));
        folders.OnActivate += (sender, args) => Observe(FoldersAsync(services, args.Parameter?.GetStrv(out _) ?? []));
        application.AddAction(folders);

        var categories = Gio.SimpleAction.New("debug-categories", GLib.VariantType.New("as"));
        categories.OnActivate += (sender, args) => Observe(CategoriesAsync(services, args.Parameter?.GetStrv(out _) ?? []));
        application.AddAction(categories);
    }

    private static async Task<MailAccount> AccountAsync(IServiceProvider provider, string address)
        => (await provider.GetRequiredService<IAccountService>().GetAccountsAsync())
            .FirstOrDefault(account => string.Equals(account.Address, address, StringComparison.OrdinalIgnoreCase))
           ?? throw new InvalidOperationException($"No account '{address}'.");

    private static async Task FoldersAsync(Func<IServiceProvider?> services, string[] parts)
    {
        var provider = services() ?? throw new InvalidOperationException("The app has not started.");
        switch (parts)
        {
            case ["open", var address]:
                await provider.GetRequiredService<ISettingsWindowPresenter>().ShowAsync(WinoPage.FolderCustomizationPage, (await AccountAsync(provider, address)).Id);
                break;
            case ["move", var folder, var direction]:
                Serilog.Log.Information("Debug action: folder move {Result}.",
                    FolderCustomizationPage.DebugCurrent?.DebugMove(folder, direction == "up" ? -1 : 1) ?? false);
                break;
            case ["page", var path]:
                DebugActions.WriteText(path, FolderCustomizationPage.DebugCurrent?.DebugDescribe() ?? "no page\n");
                break;
            case ["state", var address, var path]:
            {
                var text = new StringBuilder();
                foreach (var folder in (await provider.GetRequiredService<IFolderService>().GetFoldersAsync((await AccountAsync(provider, address)).Id)).OrderBy(folder => folder.Order))
                    text.AppendLine($"{folder.FolderName}|{folder.DisplayName}|sticky={folder.IsSticky}|hidden={folder.IsHidden}|order={folder.Order}|{folder.SpecialFolderType}");
                DebugActions.WriteText(path, text.ToString());
                break;
            }
            default:
                throw new ArgumentException($"Unknown debug-folder-customization operation '{string.Join(' ', parts)}'.");
        }
    }

    private static async Task CategoriesAsync(Func<IServiceProvider?> services, string[] parts)
    {
        var provider = services() ?? throw new InvalidOperationException("The app has not started.");
        var service = provider.GetRequiredService<IMailCategoryService>();
        switch (parts)
        {
            case ["open", var address]:
                await provider.GetRequiredService<ISettingsWindowPresenter>().ShowAsync(WinoPage.MailCategoryManagementPage, (await AccountAsync(provider, address)).Id);
                break;
            case ["seed", var address, var name, var background, var foreground]:
            {
                var created = await service.CreateCategoryAsync(new MailCategory
                {
                    Id = Guid.NewGuid(),
                    MailAccountId = (await AccountAsync(provider, address)).Id,
                    Name = name,
                    BackgroundColorHex = background,
                    TextColorHex = foreground,
                    Source = MailCategorySource.Local,
                });
                Serilog.Log.Information("Debug action: category {Name} created ({Id}).", created.Name, created.Id);
                break;
            }
            case ["list", var address, var path]:
            {
                var text = new StringBuilder();
                foreach (var category in await service.GetCategoriesAsync((await AccountAsync(provider, address)).Id))
                    text.AppendLine($"{category.Name}|{category.BackgroundColorHex}|{category.TextColorHex}|favorite={category.IsFavorite}|{category.Source}");
                DebugActions.WriteText(path, text.ToString());
                break;
            }
            case ["page", var path]:
                DebugActions.WriteText(path, MailCategoryManagementPage.DebugCurrent?.DebugDescribe() ?? "no page\n");
                break;
            case ["clear", var address]:
            {
                var removed = 0;
                foreach (var category in await service.GetCategoriesAsync((await AccountAsync(provider, address)).Id))
                {
                    if (category.Name?.StartsWith("Lab ", StringComparison.Ordinal) != true) continue;
                    await service.DeleteCategoryAsync(category.Id);
                    removed++;
                }
                Serilog.Log.Information("Debug action: {Count} lab categories deleted.", removed);
                break;
            }
            default:
                throw new ArgumentException($"Unknown debug-categories operation '{string.Join(' ', parts)}'.");
        }
    }

    private static async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-folder-customization/debug-categories failed."); }
    }
}
#endif
