using Wino.Mail.ViewModels.Messages;

namespace Wino.Mail.Linux.Views.Mail;

// A draft the list shows (new mail into a listed Drafts folder, a reply threaded with its original) is selected so the
// reading pane opens it in the composer (Mac SelectItemWhenReadyAsync). A collapsed thread is expanded first.
internal sealed partial class MailListView
{
    partial void SetUpDraftSelection() => Subscribe<SelectMailItemContainerEvent>(message => Observe(SelectDraftWhenReadyAsync(message)));

    private async Task SelectDraftWhenReadyAsync(SelectMailItemContainerEvent message)
    {
        for (var attempt = 0; attempt < 15 && !IsReleased; attempt++)
        {
            var selected = false;
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                if (IsReleased) return;
                ApplyNow(force: true);
                foreach (var entry in _shown)
                {
                    var row = entry.Row;
                    if (row.SourceItem.StableId == message.MailUniqueId && !row.IsThreadHead)
                    {
                        SelectEntries([entry], message.ScrollToItem);
                        selected = true;
                        return;
                    }
                    if (row.IsThreadHead && !row.IsExpanded && row.LeafItems.Any(leaf => leaf.StableId == message.MailUniqueId))
                    {
                        ToggleThread(entry);
                        return;
                    }
                }
            });
            if (selected) return;
            await Task.Delay(200);
        }
    }
}
