namespace Wino.Mail.Controls.Core.VoiceBubble;

public enum VoiceBubbleState
{
    Hidden,
    Listening,
    Analyzing,
    Confirmation,
    Success,
    Failed,

    /// <summary>A question the plan needs answered before it runs, with a list to pick the answer from.</summary>
    Question,
}

public enum VoiceBubbleExitAnimation
{
    Collapse,
    Slide,
    Fade,
}

/// <summary>A run of bubble text; strong runs render in semibold.</summary>
public sealed class VoiceBubbleSpan
{
    public VoiceBubbleSpan(string text, bool isStrong)
    {
        Text = text;
        IsStrong = isStrong;
    }

    public string Text { get; }

    public bool IsStrong { get; }
}

/// <summary>One line of bubble text made of plain and strong runs.</summary>
public sealed class VoiceBubbleText
{
    public VoiceBubbleText(IReadOnlyList<VoiceBubbleSpan> spans)
    {
        Spans = spans;
    }

    public IReadOnlyList<VoiceBubbleSpan> Spans { get; }

    public string PlainText => string.Concat(Spans.Select(static span => span.Text));

    /// <summary>
    /// Builds text from a string that marks strong runs with double asterisks,
    /// for example <c>Delete **12 mails** in **Promotions**?</c>.
    /// </summary>
    public static VoiceBubbleText FromMarked(string marked)
    {
        var spans = new List<VoiceBubbleSpan>();
        var parts = marked.Split("**");

        for (var index = 0; index < parts.Length; index++)
        {
            if (parts[index].Length > 0)
            {
                spans.Add(new VoiceBubbleSpan(parts[index], index % 2 == 1));
            }
        }

        return new VoiceBubbleText(spans);
    }
}

/// <summary>
/// A step of a plan that the bubble asks the user to confirm. <see cref="Number"/> is its place in the whole plan;
/// without one the bubble numbers steps in the order it shows them.
/// </summary>
public sealed class VoiceBubbleStep
{
    public VoiceBubbleStep(VoiceBubbleText text, string? detail = null, int? number = null)
    {
        Text = text;
        Detail = detail;
        Number = number;
    }

    public VoiceBubbleText Text { get; }

    public string? Detail { get; }

    public int? Number { get; }
}

/// <summary>What a step group's header shows.</summary>
public enum VoiceBubbleStepGroupKind
{
    /// <summary>One account: its picture, name and address.</summary>
    Account,

    /// <summary>Several accounts: their pictures stacked, with a title such as "All accounts".</summary>
    Accounts,

    /// <summary>An account the user named that could not be matched: a warning glyph and the question.</summary>
    Unresolved,

    /// <summary>No header.</summary>
    None,
}

/// <summary>
/// The steps of a plan that act on the same account, under a header that names it, and the parts of the request for
/// that account that can't be done (<see cref="Notices"/>, shown as banners, never as steps). When several groups
/// have steps, the user accepts each with its check box; <see cref="IsSelected"/> holds that choice.
/// </summary>
public sealed class VoiceBubbleStepGroup
{
    public VoiceBubbleStepGroup(
        VoiceBubbleStepGroupKind kind,
        string? title,
        string? subtitle,
        IReadOnlyList<AccountIcon.IAccountIconInfo> accounts,
        IReadOnlyList<VoiceBubbleStep> steps,
        IReadOnlyList<string>? notices = null)
    {
        Kind = kind;
        Title = title;
        Subtitle = subtitle;
        Accounts = accounts;
        Steps = steps;
        Notices = notices ?? Array.Empty<string>();
    }

    public IReadOnlyList<string> Notices { get; }

    /// <summary>Whether the user accepts this group's steps. Groups start accepted.</summary>
    public bool IsSelected { get; set; } = true;

    /// <summary>Only a group with steps can be accepted.</summary>
    public bool CanSelect => Steps.Count > 0;

    public VoiceBubbleStepGroupKind Kind { get; }

    public string? Title { get; }

    public string? Subtitle { get; }

    public IReadOnlyList<AccountIcon.IAccountIconInfo> Accounts { get; }

    public IReadOnlyList<VoiceBubbleStep> Steps { get; }
}

/// <summary>
/// An answer the user can pick in <see cref="VoiceBubbleState.Question"/>, or, with <see cref="IsHeader"/>, the title
/// of the account the choices under it belong to. Headers can't be picked.
/// </summary>
public sealed class VoiceBubbleChoice
{
    public VoiceBubbleChoice(string key, string title, string? detail, AccountIcon.IAccountIconInfo? account, bool isHeader = false)
    {
        Key = key;
        Title = title;
        Detail = detail;
        Account = account;
        IsHeader = isHeader;
    }

    public string Key { get; }

    public string Title { get; }

    public string? Detail { get; }

    public AccountIcon.IAccountIconInfo? Account { get; }

    public bool IsHeader { get; }

    public bool IsItem => !IsHeader;

    public bool HasDetail => !IsHeader && !string.IsNullOrWhiteSpace(Detail);

    public bool HasItemAccount => !IsHeader && Account is not null;
}
