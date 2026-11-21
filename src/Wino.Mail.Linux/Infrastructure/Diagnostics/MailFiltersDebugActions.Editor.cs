#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Linux.Views.Settings;
using Wino.Mail.ViewModels;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

// ST-13b: ['edit', address, name|'new'], and on the open editor ['editor-state', path], ['editor-set', name|source|match, value],
// ['editor-condition', index|'add', Field, Operator, value], ['editor-action', index|'add', ActionType, folder|'-'],
// ['editor-remove', 'condition'|'action', index], ['editor-save'].
internal static partial class MailFiltersDebugActions
{
    private static partial async Task<bool> RunEditorAsync(IServiceProvider provider, string[] parts)
    {
        if (parts is ["edit", var address, var name])
        {
            var account = await AccountAsync(provider, address);
            Guid? filterId = null;
            if (name != "new")
            {
                var filter = (await provider.GetRequiredService<IMailFilterService>().GetFiltersAsync(account.Id))
                    .FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException($"No filter '{name}'.");
                filterId = filter.Id;
            }
            await provider.GetRequiredService<ISettingsWindowPresenter>().ShowAsync(WinoPage.MailFilterEditorPage, new MailFilterEditorNavigationParameter(account.Id, filterId));
            return true;
        }

        if (parts.Length == 0 || !parts[0].StartsWith("editor-", StringComparison.Ordinal)) return false;
        var page = MailFilterEditorPage.DebugCurrent ?? throw new InvalidOperationException("No filter editor is open.");
        switch (parts)
        {
            case ["editor-state", var path]:
                DebugActions.WriteText(path, page.DebugDescribe());
                break;
            case ["editor-set", var kind, var value]:
                Serilog.Log.Information("Debug action: filter editor set {Kind} {Result}.", kind, page.DebugSet(kind, value));
                break;
            case ["editor-condition", var target, var field, var op, var value]:
                page.DebugCondition(target, field, op, value);
                break;
            case ["editor-action", var target, var type, var folder]:
                page.DebugAction(target, type, folder);
                break;
            case ["editor-remove", var kind, var index]:
                page.DebugRemove(kind, int.Parse(index));
                break;
            case ["editor-save"]:
                page.DebugSave();
                break;
            default:
                return false;
        }
        return true;
    }
}
#endif
