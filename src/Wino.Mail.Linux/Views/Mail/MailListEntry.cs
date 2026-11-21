using Wino.Mail.Controls.Core;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>One mail row in the list store. Its identity outlives the projection row it shows, so an equal-key row keeps its place, cell and selection.</summary>
internal sealed class MailListEntry(MailListRow row, string key)
{
    public string Key { get; } = key;

    public MailListRow Row { get; set; } = row;

    public int GroupOrdinal { get; set; }

    public string GroupTitle { get; set; } = string.Empty;

    /// <summary>The apply pass that last placed this entry; a row the projection lists twice is shown once.</summary>
    public int Pass { get; set; }
}
