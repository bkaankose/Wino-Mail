using System.Text;
using Wino.Mapi.Wire;

namespace Wino.Mapi.Rops;

/// <summary>
/// RopLogon (MS-OXCSTOR 2.2.1.1). The first ROP of any session, and the one worth getting alone:
/// a private-mailbox logon response hands back the whole set of special folder ids, so the Inbox
/// does not have to be searched for afterwards.
/// </summary>
public static class RopLogon
{
    public const byte RopId = 0xFE;

    [Flags]
    public enum LogonFlags : byte
    {
        Private = 0x01,
    }

    /// <summary>OpenFlags (MS-OXCSTOR 2.2.1.1.1).</summary>
    public const uint OpenFlagPublic = 0x00000002;
    public const uint OpenFlagUsePerMdbReplidMapping = 0x01000000;

    /// <summary>
    /// A private logon to <paramref name="mailboxDn"/>, or, when <paramref name="publicStore"/>, a
    /// logon to the public folder store (LogonFlags without Private, OpenFlags PUBLIC, no ESSDN).
    /// </summary>
    public static byte[] BuildRequest(string mailboxDn, byte outputHandleIndex = 0, bool publicStore = false)
    {
        var essdn = publicStore ? [] : Encoding.ASCII.GetBytes(mailboxDn);

        var rop = new RopWriter();
        rop.UInt8(RopId);
        rop.UInt8(0);                       // LogonId
        rop.UInt8(outputHandleIndex);       // OutputHandleIndex
        rop.UInt8(publicStore ? (byte)0 : (byte)LogonFlags.Private);
        rop.UInt32(OpenFlagUsePerMdbReplidMapping | (publicStore ? OpenFlagPublic : 0));
        rop.UInt32(0);                      // StoreState
        if (publicStore)
        {
            rop.UInt16(0);                  // EssdnSize: none for a public logon
        }
        else
        {
            rop.UInt16((ushort)(essdn.Length + 1));
            rop.Bytes(essdn);
            rop.UInt8(0);                   // ESSDN is null-terminated
        }

        return rop.ToArray();
    }

    /// <summary>Public logon folder ids (MS-OXCSTOR 2.2.1.1.4): Root, IPM subtree, Non-IPM subtree, EForms registry, ...</summary>
    public const int PublicIpmSubtreeIndex = 1;

    /// <summary>
    /// Special folder ids, in the order the logon response returns them (MS-OXCSTOR 2.2.1.1.3).
    /// </summary>
    public static readonly string[] FolderNames =
    [
        "Root", "Deferred Action", "Spooler Queue", "IPM Subtree", "Inbox", "Outbox",
        "Sent Items", "Deleted Items", "Common Views", "Schedule", "Search", "Views", "Shortcuts",
    ];

    public const int RootIndex = 0;
    public const int IpmSubtreeIndex = 3;
    public const int InboxIndex = 4;
    public const int OutboxIndex = 5;
    public const int SentItemsIndex = 6;
    public const int DeletedItemsIndex = 7;

    public static LogonResponse ParseResponse(byte[] rops)
    {
        var reader = new RopReader(rops);
        RopExecute.ExpectSuccess(reader, RopId, nameof(RopLogon));

        var logonFlags = reader.UInt8();
        var folderIds = new ulong[13];
        for (var i = 0; i < folderIds.Length; i++)
        {
            folderIds[i] = reader.UInt64();
        }

        // A public logon response (2.2.1.1.4) has no ResponseFlags/MailboxGuid: ReplId, ReplGuid, PerUserGuid.
        if ((logonFlags & (byte)LogonFlags.Private) == 0)
        {
            var publicReplId = reader.UInt16();
            var publicReplGuid = reader.Bytes(16).ToArray();
            var perUserGuid = reader.Bytes(16).ToArray();
            return new LogonResponse
            {
                LogonFlags = logonFlags,
                FolderIds = folderIds,
                ReplId = publicReplId,
                ReplGuid = new Guid(publicReplGuid),
                MailboxGuid = new Guid(perUserGuid),
                IsPublicStore = true,
            };
        }

        var responseFlags = reader.UInt8();
        var mailboxGuid = reader.Bytes(16).ToArray();
        var replId = reader.UInt16();
        var replGuid = reader.Bytes(16).ToArray();

        return new LogonResponse
        {
            LogonFlags = logonFlags,
            FolderIds = folderIds,
            ResponseFlags = responseFlags,
            MailboxGuid = new Guid(mailboxGuid),
            ReplId = replId,
            ReplGuid = new Guid(replGuid),
        };
    }
}

public sealed class LogonResponse
{
    public byte LogonFlags { get; init; }
    public ulong[] FolderIds { get; init; } = [];
    public byte ResponseFlags { get; init; }
    public Guid MailboxGuid { get; init; }
    public ushort ReplId { get; init; }
    public Guid ReplGuid { get; init; }

    /// <summary>True for a public folder store logon; the folder ids then follow the public layout.</summary>
    public bool IsPublicStore { get; init; }

    /// <summary>The public folders' IPM subtree (public logon only).</summary>
    public ulong PublicIpmSubtreeFolderId => FolderIds[RopLogon.PublicIpmSubtreeIndex];

    public ulong RootFolderId => FolderIds[RopLogon.RootIndex];
    public ulong IpmSubtreeFolderId => FolderIds[RopLogon.IpmSubtreeIndex];
    public ulong InboxFolderId => FolderIds[RopLogon.InboxIndex];
    public ulong OutboxFolderId => FolderIds[RopLogon.OutboxIndex];
    public ulong SentItemsFolderId => FolderIds[RopLogon.SentItemsIndex];
    public ulong DeletedItemsFolderId => FolderIds[RopLogon.DeletedItemsIndex];
}
