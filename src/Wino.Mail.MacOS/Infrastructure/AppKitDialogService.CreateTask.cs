using System.Linq;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Tasks;
using Wino.Mail.MacOS.Views.Dialogs;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Task from a mail or an event (Windows CreateTaskFromItemDialog).</summary>
public sealed partial class AppKitDialogService
{
    public Task<TaskFromItemResult?> ShowCreateTaskFromItemDialogAsync(TaskFromItemDraft draft)
    {
        var listLabels = draft.Lists
            .Select(list => TaskListLabel(list, draft.Accounts.FirstOrDefault(account => account.Id == list.MailAccountId)))
            .ToList();
        return PresentAsync(window => new CreateTaskFromItemSheet(draft, listLabels).PresentAsync(window));
    }

    private static string TaskListLabel(AccountTaskList list, MailAccount? account)
    {
        var owner = account is null ? string.Empty : AccountLine(account);
        return owner.Length == 0 ? list.Title ?? string.Empty : $"{list.Title} — {owner}";
    }
}
