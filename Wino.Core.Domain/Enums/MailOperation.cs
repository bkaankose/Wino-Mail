﻿namespace Wino.Core.Domain.Enums
{
    // Synchronizer requests.
    public enum MailSynchronizerOperation
    {
        MarkRead,
        Move,
        Delete, // Hard delete.
        CreateDraft,
        Send,
        ChangeFlag,
        AlwaysMoveTo,
        MoveToFocused,
        Archive,
        RenameFolder,
        EmptyFolder,
        MarkFolderRead,
    }

    // UI requests
    public enum MailOperation
    {
        None,
        Archive,
        UnArchive,
        SoftDelete,
        HardDelete,
        Move,
        MoveToJunk,
        MoveToFocused,
        MoveToOther,
        AlwaysMoveToOther,
        AlwaysMoveToFocused,
        SetFlag,
        ClearFlag,
        MarkAsRead,
        MarkAsUnread,
        MarkAsNotJunk,
        Seperator,
        Ignore,
        Reply,
        ReplyAll,
        Zoom,
        SaveAs,
        Find,
        Forward,
        DarkEditor,
        LightEditor,
        Print,
        DiscardLocalDraft,
        Navigate // For toast activation
    }
}
