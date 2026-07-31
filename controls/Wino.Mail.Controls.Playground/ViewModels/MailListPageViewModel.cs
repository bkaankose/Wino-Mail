using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.Playground.Models;

namespace Wino.Mail.Controls.Playground.ViewModels;

public sealed class MailListPageViewModel
{
    public MailListCollection<SampleMailItem> Items { get; } = new();

    public MailListProjectionOptions ProjectionOptions { get; } = new()
    {
        GroupMode = MailListGroupMode.None,
        IsThreadingEnabled = true,
        IsPinnedFirst = true,
        SortMode = MailListSortMode.Date,
    };

    public MailListPageViewModel()
    {
        var now = DateTimeOffset.Now;
        Items.AddRange(new[]
        {
            new SampleMailItem("Release checklist", "release", now.AddMinutes(-4), isPinned: true),
            new SampleMailItem("Release checklist — final review", "release", now.AddMinutes(-17)),
            new SampleMailItem("Design notes", null, now.AddHours(-2)),
            new SampleMailItem("Accessibility feedback", "accessibility", now.AddDays(-1)),
            new SampleMailItem("Accessibility feedback — follow-up", "accessibility", now.AddDays(-1).AddMinutes(-9)),
        });
    }
}
