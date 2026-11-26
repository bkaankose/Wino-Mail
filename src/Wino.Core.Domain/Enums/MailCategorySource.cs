namespace Wino.Core.Domain.Enums;

public enum MailCategorySource
{
    Local,
    Outlook,
    Exchange,

    /// <summary>A colored Gmail label shown as a category. It is read from the label folders and never stored.</summary>
    GmailLabel
}
