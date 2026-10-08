using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.MailListView;
using Wino.Mail.Controls.Playground.Models;

namespace Wino.Mail.Controls.Playground.Pages;

/// <summary>Exercises host summaries for individual messages and expandable conversations.</summary>
public sealed partial class AccessiblePlaygroundMailListView : WinoMailListView
{
    public override string GetRowAutomationName(MailListRow row)
    {
        var source = row.IsThreadHead ? row.Thread?.RepresentativeItem ?? row.SourceItem : row.SourceItem;
        if (source is not MailListPlaygroundItem mail) return base.GetRowAutomationName(row);
        var summary = $"{mail.Sender}, {mail.Subject}, {mail.CreatedAt:f}";
        return row.IsThreadHead && row.Thread is { } thread
            ? $"{summary}, {thread.Count} messages, {(thread.IsExpanded ? "expanded" : "collapsed")}" : summary;
    }
}
