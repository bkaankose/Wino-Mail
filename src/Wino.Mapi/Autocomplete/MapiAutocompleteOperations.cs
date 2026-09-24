using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wino.Mapi.Rops;
using Wino.Mapi.Rules;
using Wino.Mapi.Wire;

namespace Wino.Mapi.Autocomplete;

/// <summary>
/// Reading and writing Outlook's autocomplete list where Outlook 2010 and later keep it: a hidden
/// message in the Inbox's associated contents table, with the list in one binary property on it.
///
/// The same shelf the rules live on, so the plumbing to find a hidden message by its class and to
/// move a large property in and out of one is already here and is reused rather than rebuilt.
/// </summary>
public static class MapiAutocompleteOperations
{
    /// <summary>The class and subject of the hidden message that holds the list.</summary>
    public const string AutocompleteMessageClass = "IPM.Configuration.Autocomplete";

    /// <summary>PR_ROAMING_BINARYSTREAM: the list itself.</summary>
    public const uint RoamingBinary = 0x7C090102;

    private const uint MessageClassTag = PropertyTags.MessageClass;
    private const uint SubjectTag = PropertyTags.Subject;

    private const int Slots = 8;

    /// <summary>The hidden message holding the list, or null when the mailbox has never had one.</summary>
    public static async Task<ulong?> FindAsync(MapiSession session, ulong inboxFolderId, CancellationToken cancellationToken = default)
    {
        var rows = await MapiRulesOperations.ReadFaiRuleRowsAsync(session, inboxFolderId, cancellationToken).ConfigureAwait(false);

        return rows.FirstOrDefault(r => string.Equals(r.MessageClass, AutocompleteMessageClass, StringComparison.OrdinalIgnoreCase))?.MessageId;
    }

    /// <summary>
    /// Reads the list. Null when the mailbox has none, which is the ordinary state of a mailbox
    /// Outlook has never opened - the caller starts an empty one rather than treating it as a fault.
    /// </summary>
    public static async Task<AutocompleteStream?> ReadAsync(MapiSession session, ulong inboxFolderId, CancellationToken cancellationToken = default, Action<string>? diagnostics = null)
    {
        var messageId = await FindAsync(session, inboxFolderId, cancellationToken).ConfigureAwait(false);

        if (messageId is null)
        {
            diagnostics?.Invoke("autocomplete: the mailbox has no list yet");
            return null;
        }

        var bytes = await MapiRulesOperations.ReadPropertyStreamAsync(session, inboxFolderId, messageId.Value, RoamingBinary, cancellationToken).ConfigureAwait(false);

        if (bytes.Length == 0)
        {
            diagnostics?.Invoke("autocomplete: the list message is there but carries no stream");
            return null;
        }

        var stream = AutocompleteStream.Parse(bytes);

        diagnostics?.Invoke(stream.RoundTripsExactly
            ? $"autocomplete: read {stream.Entries.Count} entries from {bytes.Length} bytes, reproduces exactly"
            : $"autocomplete: read {stream.Entries.Count} entries from {bytes.Length} bytes, DOES NOT reproduce exactly - this mailbox's list will not be written to");

        return stream;
    }

    /// <summary>
    /// Writes the whole list back, creating the hidden message if the mailbox has none.
    ///
    /// Whole, because the format says so: the supported way to change this list is to read it all,
    /// change the rows, and put it all back. There is no partial edit, and attempting one is how the
    /// metadata another Outlook depends on gets lost.
    /// </summary>
    public static async Task WriteAsync(MapiSession session, ulong inboxFolderId, AutocompleteStream stream, CancellationToken cancellationToken = default, Action<string>? diagnostics = null)
    {
        // The guard, not a formality. If what was read could not be reproduced byte for byte before
        // anything was changed, then this code does not fully understand that mailbox's list, and
        // writing would replace something it only partly read with something it partly invented.
        if (!stream.RoundTripsExactly)
        {
            diagnostics?.Invoke("autocomplete: refusing to write, because the list as read does not reproduce exactly");
            return;
        }

        var payload = stream.Serialize();
        var messageId = await FindAsync(session, inboxFolderId, cancellationToken).ConfigureAwait(false);

        var handles = MapiSession.MergeHandles([session.LogonHandle], [], Slots);

        if (messageId is null)
        {
            var (created, returnedNew) = await session.ExecuteAsync(
                RopMessageWrite.BuildCreateMessage(0, 3, inboxFolderId, associated: true), handles, cancellationToken).ConfigureAwait(false);

            RopMessageWrite.ParseCreateMessage(new RopReader(created));
            handles = MapiSession.MergeHandles(handles, returnedNew, Slots);

            // A new list message has to say what it is, in both the places Outlook looks.
            await MapiMessageComposer.WritePropertyAsync(session, handles, 3, 4, MessageClassTag, Encoding.Unicode.GetBytes(AutocompleteMessageClass + "\0"), isUnicodeString: true, cancellationToken).ConfigureAwait(false);
            await MapiMessageComposer.WritePropertyAsync(session, handles, 3, 4, SubjectTag, Encoding.Unicode.GetBytes(AutocompleteMessageClass + "\0"), isUnicodeString: true, cancellationToken).ConfigureAwait(false);

            diagnostics?.Invoke("autocomplete: created the list message");
        }
        else
        {
            var (opened, returnedOpen) = await session.ExecuteAsync(
                RopMessage.BuildOpenMessage(inboxFolderId, messageId.Value, 0, 3, RopMessageOps.OpenReadWrite), handles, cancellationToken).ConfigureAwait(false);

            RopMessage.ParseOpenMessage(new RopReader(opened));
            handles = MapiSession.MergeHandles(handles, returnedOpen, Slots);
        }

        try
        {
            await MapiMessageComposer.WritePropertyAsync(session, handles, 3, 4, RoamingBinary, payload, isUnicodeString: false, cancellationToken).ConfigureAwait(false);

            var (saved, _) = await session.ExecuteAsync(RopMessageOps.BuildSaveChangesMessage(4, 3), handles, cancellationToken, interactive: true).ConfigureAwait(false);
            RopMessageOps.ParseSaveChangesMessage(new RopReader(saved));

            diagnostics?.Invoke($"autocomplete: wrote {stream.Entries.Count} entries in {payload.Length} bytes");
        }
        finally
        {
            await session.ReleaseAsync(handles[3], cancellationToken).ConfigureAwait(false);
        }
    }
}
