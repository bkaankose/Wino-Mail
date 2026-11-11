using Wino.Agent.Runtime.Plan;
using Wino.Agent.Runtime.Reading;
using Wino.Agent.Runtime.Resolve;
using Wino.Core.Domain.Enums;

namespace Wino.Agent.Runtime.Policy;

/// <summary>Deterministic risk and confirmation per step, after resolution (plan 4.6).</summary>
public static class PlanPolicy
{
    public sealed record Assessment(RiskLevel Risk, bool NeedsConfirmation, bool Permanent);

    public static Assessment Assess(AgentAction action, StepTarget target, StepArgs args, int? count, DomainIndex index)
    {
        var permanent = false;
        var allAccounts = target is StepTarget.Mails { AccountId: null } or StepTarget.Events { AccountId: null } or StepTarget.Tasks { AccountId: null };
        var allFolders = target is StepTarget.Mails { Query.AllFolders: true, Query.Limit: 0 } m && m.Query.SenderAddresses.Count == 0 && m.Query.SenderDomains.Count == 0 && m.Query.SenderWords is null && m.Query.Text is null;
        var n = count ?? 0;
        var risk = action switch
        {
            AgentAction.SearchMail or AgentAction.FindEvents or AgentAction.FindTasks or AgentAction.FindContacts or AgentAction.Summarize
                or AgentAction.Translate or AgentAction.CopyCode or AgentAction.AnalyzeMail or AgentAction.SyncMail or AgentAction.SyncFolder
                or AgentAction.Undo or AgentAction.JoinMeeting => RiskLevel.None,

            AgentAction.MarkRead or AgentAction.MarkUnread => n >= 200 ? RiskLevel.High : RiskLevel.Low,
            AgentAction.Flag or AgentAction.Unflag or AgentAction.Pin or AgentAction.Unpin or AgentAction.Categorize or AgentAction.Uncategorize
                or AgentAction.MoveToFocused or AgentAction.MoveToOther or AgentAction.CreateDraft or AgentAction.CreateReplyDraft
                or AgentAction.CreateEvent or AgentAction.CreateTask or AgentAction.FavoriteContact or AgentAction.UnfavoriteContact
                or AgentAction.AddTaskStep or AgentAction.CompleteTask or AgentAction.ReopenTask or AgentAction.PinFolder or AgentAction.UnpinFolder
                or AgentAction.CreateContact or AgentAction.MarkNotJunk => RiskLevel.Low,

            AgentAction.Archive => n >= 100 ? RiskLevel.High : RiskLevel.Medium,
            AgentAction.Delete => n >= 20 || (allFolders && allAccounts) || DeletesFromTrash(target, index, out permanent) ? RiskLevel.High : RiskLevel.Medium,
            AgentAction.MoveToFolder or AgentAction.MarkJunk or AgentAction.Unarchive or AgentAction.UpdateEvent or AgentAction.UpdateTask
                or AgentAction.SetEventShowAs or AgentAction.SetEventReminder or AgentAction.AddContactToList or AgentAction.CreateFolder
                or AgentAction.CreateTaskList or AgentAction.RenameFolder or AgentAction.RenameTaskList or AgentAction.Unsubscribe
                or AgentAction.MarkFolderRead or AgentAction.DeleteTask or AgentAction.CategorizeContact => RiskLevel.Medium,
            AgentAction.RespondToEvent => args is StepArgs.EventChangeArgs { Response: "Decline" } ? RiskLevel.High : RiskLevel.Medium,
            AgentAction.DeleteEvent => RiskLevel.High,
            // A rule is a change the user confirms like any write; one that deletes future mails gets its own confirmation.
            AgentAction.CreateMailRule => args is StepArgs.MailRuleArgs { Deletes: true } ? RiskLevel.High : RiskLevel.Medium,
            AgentAction.ChangeSetting => RiskLevel.Low,
            AgentAction.OpenView => RiskLevel.None,

            AgentAction.EmptyFolder or AgentAction.SendDrafts or AgentAction.DeleteContact or AgentAction.DeleteFolder
                or AgentAction.DeleteTaskList or AgentAction.RemoveContactFromList => RiskLevel.High,
            _ => RiskLevel.Medium,
        };

        if (action == AgentAction.EmptyFolder) permanent = true;
        // Any data change across every account is High.
        if (risk is RiskLevel.Low or RiskLevel.Medium && allAccounts && !ActionCatalog.Creates(action) && !ActionCatalog.IsFind(action) && n >= 20) risk = RiskLevel.High;
        return new Assessment(risk, risk == RiskLevel.High, permanent);
    }

    private static bool DeletesFromTrash(StepTarget target, DomainIndex index, out bool permanent)
    {
        permanent = false;
        if (target is not StepTarget.Mails { Query.FolderIds.Count: > 0 } m) return false;
        var trash = index.Containers.Where(c => c.Kind == ItemKind.Folder && c.Special == SpecialFolderType.Deleted).Select(c => c.Id).ToHashSet();
        permanent = m.Query.FolderIds.All(trash.Contains);
        return permanent;
    }
}
