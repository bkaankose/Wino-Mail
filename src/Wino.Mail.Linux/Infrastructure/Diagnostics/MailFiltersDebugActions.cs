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
/// <c>debug-filters</c> (<c>as</c>): <c>['open', address]</c>; <c>['seed', address, name, subjectText, targetFolder]</c>
/// (a Wino filter on the Inbox: subject contains the text -> move to the folder); <c>['list', address, path]</c>;
/// <c>['page', path]</c> (the open list page); <c>['clear', address]</c> (names starting with "Lab ").
/// </summary>
internal static partial class MailFiltersDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-filters", GLib.VariantType.New("as"));
        action.OnActivate += (sender, args) => Observe(RunAsync(services, args.Parameter?.GetStrv(out _) ?? []));
        application.AddAction(action);
    }

    private static async Task<MailAccount> AccountAsync(IServiceProvider provider, string address)
        => (await provider.GetRequiredService<IAccountService>().GetAccountsAsync())
            .FirstOrDefault(account => string.Equals(account.Address, address, StringComparison.OrdinalIgnoreCase))
           ?? throw new InvalidOperationException($"No account '{address}'.");

    private static async Task<MailItemFolder> FolderAsync(IServiceProvider provider, Guid accountId, Func<MailItemFolder, bool> match, string what)
        => (await provider.GetRequiredService<IFolderService>().GetFoldersAsync(accountId)).FirstOrDefault(match)
           ?? throw new InvalidOperationException($"No folder '{what}'.");

    private static async Task RunAsync(Func<IServiceProvider?> services, string[] parts)
    {
        var provider = services() ?? throw new InvalidOperationException("The app has not started.");
        var filters = provider.GetRequiredService<IMailFilterService>();
        switch (parts)
        {
            case ["open", var address]:
                await provider.GetRequiredService<ISettingsWindowPresenter>().ShowAsync(WinoPage.MailFiltersPage, (await AccountAsync(provider, address)).Id);
                break;
            case ["seed", var address, var name, var subject, var target]:
            {
                var account = await AccountAsync(provider, address);
                var inbox = await FolderAsync(provider, account.Id, folder => folder.SpecialFolderType == SpecialFolderType.Inbox, "Inbox");
                var destination = await FolderAsync(provider, account.Id, folder => string.Equals(folder.FolderName, target, StringComparison.OrdinalIgnoreCase), target);
                var existing = await filters.GetFiltersAsync(account.Id);
                var created = await filters.CreateFilterAsync(new MailFilter
                {
                    Id = Guid.NewGuid(),
                    MailAccountId = account.Id,
                    CreatedAtUtc = DateTime.UtcNow,
                    IsWinoCreated = true,
                    Name = name,
                    ManagementType = MailFilterManagementType.WinoLocal,
                    SourceRemoteFolderId = inbox.RemoteFolderId,
                    MatchMode = MailFilterMatchMode.All,
                    IsEnabled = true,
                    Sequence = existing.Count == 0 ? 0 : existing.Max(filter => filter.Sequence) + 1,
                    Conditions = [new MailFilterCondition { Order = 0, Field = MailFilterConditionField.Subject, Operator = MailFilterConditionOperator.Contains, Value = subject }],
                    Actions = [new MailFilterAction { Order = 0, Type = MailFilterActionType.Move, TargetRemoteFolderId = destination.RemoteFolderId }],
                });
                Serilog.Log.Information("Debug action: filter {Name} created ({Id}).", created.Name, created.Id);
                break;
            }
            case ["list", var address, var path]:
            {
                var text = new StringBuilder();
                foreach (var filter in (await filters.GetFiltersAsync((await AccountAsync(provider, address)).Id)).OrderBy(filter => filter.Sequence))
                {
                    text.Append($"{filter.Sequence}|{filter.Name}|enabled={filter.IsEnabled}|{filter.ManagementType}|source={filter.SourceRemoteFolderId}|match={filter.MatchMode}|stop={filter.StopProcessing}");
                    foreach (var condition in filter.Conditions.OrderBy(condition => condition.Order)) text.Append($"|if {condition.Field} {condition.Operator} '{condition.Value}'");
                    foreach (var action in filter.Actions.OrderBy(action => action.Order)) text.Append($"|then {action.Type} {action.TargetRemoteFolderId}");
                    text.AppendLine();
                }
                DebugActions.WriteText(path, text.ToString());
                break;
            }
            case ["page", var path]:
                DebugActions.WriteText(path, MailFiltersPage.DebugCurrent?.DebugDescribe() ?? "no page\n");
                break;
            case ["clear", var address]:
            {
                var removed = 0;
                foreach (var filter in await filters.GetFiltersAsync((await AccountAsync(provider, address)).Id))
                {
                    if (filter.Name?.StartsWith("Lab ", StringComparison.Ordinal) != true) continue;
                    await filters.DeleteFilterAsync(filter.Id);
                    removed++;
                }
                Serilog.Log.Information("Debug action: {Count} lab filters deleted.", removed);
                break;
            }
            default:
                if (!await RunEditorAsync(provider, parts))
                    throw new ArgumentException($"Unknown debug-filters operation '{string.Join(' ', parts)}'.");
                break;
        }
    }

    private static partial Task<bool> RunEditorAsync(IServiceProvider provider, string[] parts);

    private static async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-filters failed."); }
    }
}
#endif
