using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Tasks;
using Wino.Mail.Linux.Views.Dialogs;

namespace Wino.Mail.Linux.Infrastructure;

internal sealed partial class GtkDialogService
{
    public Task<TaskFromItemResult?> ShowCreateTaskFromItemDialogAsync(TaskFromItemDraft draft)
    {
        if (draft.Lists.Count == 0) return Task.FromResult<TaskFromItemResult?>(null);
        var multipleAccounts = draft.Lists.Select(static list => list.MailAccountId).Distinct().Skip(1).Any();
        var labels = draft.Lists.Select(list => TaskListLabel(list, multipleAccounts ? draft.Accounts.FirstOrDefault(account => account.Id == list.MailAccountId) : null)).ToArray();
        return PresentAsync(owner => CreateTaskFromItemDialog.ShowAsync(owner, draft, labels), null);
    }

    private static string TaskListLabel(AccountTaskList list, MailAccount? account)
    {
        var owner = account is null ? string.Empty : AccountLine(account);
        return owner.Length == 0 ? list.Title ?? string.Empty : $"{list.Title} — {owner}";
    }
}
