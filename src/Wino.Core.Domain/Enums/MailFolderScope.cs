namespace Wino.Core.Domain.Enums;

/// <summary>The IMAP namespace (RFC 2342) a folder belongs to. Folders of every other provider are personal.</summary>
public enum MailFolderScope
{
    Personal = 0,
    OtherUser = 1,
    Shared = 2
}
