using Wino.Mail.Controls.Core.AccountIcon;
using Wino.Mail.Controls.Core.VoiceBubble;

namespace Wino.Mail.Controls.Playground.Models;

internal sealed class VoiceBubbleScenario
{
    public required string Name { get; init; }

    public required string Spoken { get; init; }

    public VoiceBubbleText? Confirmation { get; init; }

    public VoiceBubbleStep[] Steps { get; init; } = [];

    public VoiceBubbleStepGroup[] Groups { get; init; } = [];

    public VoiceBubbleText? Success { get; init; }

    public VoiceBubbleFailure? Failure { get; init; }

    public bool NeedsConfirmation => Confirmation is not null || Steps.Length > 0 || Groups.Length > 0;

    public static VoiceBubbleScenario[] All { get; } =
    [
        new()
        {
            Name = "Mark read",
            Spoken = "Mark every unread mail from Microsoft from last week as read",
            Success = VoiceBubbleText.FromMarked("Marked **3 mails** from Microsoft as read"),
        },
        new()
        {
            Name = "Delete",
            Spoken = "Delete the promotions from last month",
            Confirmation = VoiceBubbleText.FromMarked("Delete **12 mails** in **Promotions**?"),
            Success = VoiceBubbleText.FromMarked("Deleted **12 mails** from Promotions"),
        },
        new()
        {
            Name = "Move and reply",
            Spoken = "Move this month's invoices to Finance and tell Ahmet I'll be back in thirty minutes because I'm stuck in the budget meeting",
            Steps =
            [
                new(VoiceBubbleText.FromMarked("Find mails with **invoice** from **this month**"), "7 found in kaan@bkaan.dev"),
                new(VoiceBubbleText.FromMarked("Move them to **Finance**")),
                new(VoiceBubbleText.FromMarked("Reply to **Ahmet**"), "“I’ll be back in thirty minutes, I’m stuck in the budget meeting.”"),
            ],
            Success = VoiceBubbleText.FromMarked("Moved **7 mails** to Finance and replied to **Ahmet**"),
        },
        new()
        {
            Name = "Two accounts",
            Spoken = "Delete all mails in my personal account and archive the latest mail in my Gmail inbox",
            Groups =
            [
                new(VoiceBubbleStepGroupKind.Account, "Personal", "kaan@bkaan.dev", new IAccountIconInfo[] { new AccountIconInfo(AccountIconProvider.Microsoft, AccountColorHex: "#0078D4") },
                new VoiceBubbleStep[] {
                    new(VoiceBubbleText.FromMarked("**Delete** all mails"), "Inbox", 1),
                }),
                new(VoiceBubbleStepGroupKind.Account, "Gmail", "burak@gmail.com", new IAccountIconInfo[] { new AccountIconInfo(AccountIconProvider.Google, AccountColorHex: "#EA4335") },
                new VoiceBubbleStep[] {
                    new(VoiceBubbleText.FromMarked("**Archive** the latest mail"), "Inbox", 2),
                },
                new[] { "It isn't clear which action you meant for the starred mails: Mark as read, Archive or Delete." }),
            ],
            Success = VoiceBubbleText.FromMarked("Deleted **24 mails** and archived **1 mail**"),
        },
        new()
        {
            Name = "All accounts and unclear account",
            Spoken = "Mark unread mails from Elif as read everywhere and flag the contract mail in my work account",
            Groups =
            [
                new(VoiceBubbleStepGroupKind.Accounts, "All accounts", "3 accounts",
                new IAccountIconInfo[]
                {
                    new AccountIconInfo(AccountIconProvider.Microsoft, AccountColorHex: "#0078D4"),
                    new AccountIconInfo(AccountIconProvider.Google, AccountColorHex: "#EA4335"),
                    new AccountIconInfo(AccountIconProvider.Imap, AccountColorHex: "#6B69D6"),
                },
                new VoiceBubbleStep[] {
                    new(VoiceBubbleText.FromMarked("**Mark as read** all mails"), "Unread · From Elif · All folders", 1),
                }),
                new(VoiceBubbleStepGroupKind.Unresolved, "Which account?", "No single account matches “work”", Array.Empty<IAccountIconInfo>(),
                new VoiceBubbleStep[] {
                    new(VoiceBubbleText.FromMarked("**Flag** all mails"), "Subject “contract” · All folders", 2),
                }),
            ],
            Success = VoiceBubbleText.FromMarked("Marked **4 mails** as read"),
        },
        new()
        {
            Name = "Add event",
            Spoken = "Put a lunch with Elif on Friday in my calendar",
            Success = VoiceBubbleText.FromMarked("Added **Lunch with Elif**, Friday 13:00"),
        },
        new()
        {
            Name = "Not possible",
            Spoken = "Order me a pizza for tonight",
            Failure = VoiceBubbleFailure.All[0],
        },
    ];
}

internal sealed class VoiceBubbleFailure
{
    public required string Name { get; init; }

    public required VoiceBubbleText Message { get; init; }

    public bool CanRetry { get; init; } = true;

    public static VoiceBubbleFailure[] All { get; } =
    [
        new() { Name = "Not something Wino can do", Message = VoiceBubbleText.FromMarked("Wino can only work with your mail, calendar, contacts and tasks.") },
        new() { Name = "Nothing heard", Message = VoiceBubbleText.FromMarked("Nothing was heard. Try again.") },
        new() { Name = "No connection", Message = VoiceBubbleText.FromMarked("Can't reach Wino. Check your connection.") },
        new() { Name = "Monthly limit reached", Message = VoiceBubbleText.FromMarked("You've used all **500** voice requests this month."), CanRetry = false },
    ];
}
