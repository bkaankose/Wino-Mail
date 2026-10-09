using AppKit;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.AppKit.Shell;
using Wino.Mail.MacOS.Views.Contacts;
using Wino.Mail.MacOS.Views.Mail;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.MacOS.Views.Shell;

/// <summary>
/// Mail drop target on the shell pane (Windows ShellMenuTemplates ItemDragOverFolder /
/// ItemDroppedOnFolder): folder rows that pass CanContinueDragDrop take the drop with the accent
/// highlight and a "Move to {folder}" caption on the drag image; the drop moves the mails through
/// <see cref="IMailShellClient.PerformMoveOperationAsync"/>. Other rows show no highlight.
/// Contacts dragged from the contacts list drop on contact-list rows (Windows ContactFilterViewModel
/// CanAccept / HandleDropAsync) with an "Add to {list}" caption; the ViewModel reports the result.
/// </summary>
internal sealed partial class ShellSidebarViewController
{
    private const nint DropOnItem = -1;
    private nint _dropRow = -1;
    private string? _dropCaption;
    private nint _dropSequence = -1;

    private void SetUpMailDrop()
    {
        _outline.RegisterForDraggedTypes([MailDragPayload.PasteboardType, ContactDragPayload.PasteboardType]);
        _outline.DraggingDestinationFeedbackStyle = NSTableViewDraggingDestinationFeedbackStyle.None;
        _outline.DragFinished = () => SetDropRow(-1);
#if DEBUG
        RegisterDropDebugCommands();
#endif
    }

    /// <summary>The folder row under the pointer, whatever position AppKit proposed (between or on rows).</summary>
    private (nint Row, IBaseFolderMenuItem? Folder) FolderAt(INSDraggingInfo info)
    {
        var row = _outline.GetRow(_outline.ConvertPointFromView(info.DraggingLocation, null));
        return FolderAtRow(row);
    }

    private (nint Row, IBaseFolderMenuItem? Folder) FolderAtRow(nint row)
    {
        if (row < 0 || row >= _outline.RowCount || _outline.ItemAtRow(row) is not Node node) return (-1, null);
        return (row, node.Item is IBaseFolderMenuItem folder && ShellPaneRows.IsEnabled(folder) ? folder : null);
    }

    private bool IsSelectedFolder(nint row, IBaseFolderMenuItem folder)
        => _outline.IsRowSelected(row) || ReferenceEquals(_selectedItem, folder);

    internal NSDragOperation ValidateMailDrop(INSDraggingInfo info)
    {
        if (ContactDragPayload.IsContactDrag(info)) return ValidateContactDrop(info);
        var mails = MailDragPayload.From(info);
        var (row, folder) = FolderAt(info);
        if (mails.Count == 0 || folder is null)
        {
            SetDropRow(-1);
            UpdateDragCaption(info, mails.Count, null, refused: false);
            return NSDragOperation.None;
        }

        if (!MailDragPayload.CanDropOn(folder, IsSelectedFolder(row, folder), mails))
        {
            SetDropRow(-1);
            UpdateDragCaption(info, mails.Count, Translator.DragCannotMoveHereCaption, refused: true);
            return NSDragOperation.None;
        }

        if (_outline.ItemAtRow(row) is { } node) _outline.SetDropItem(node, DropOnItem);
        SetDropRow(row);
        UpdateDragCaption(info, mails.Count, string.Format(Translator.DragMoveToFolderCaption, folder.FolderName), refused: false);
        return NSDragOperation.Move;
    }

    internal bool AcceptMailDrop(INSDraggingInfo info)
    {
        if (ContactDragPayload.IsContactDrag(info)) return AcceptContactDrop(info);
        var mails = MailDragPayload.From(info);
        var (row, folder) = FolderAt(info);
        SetDropRow(-1);
        if (folder is null || !MailDragPayload.CanDropOn(folder, IsSelectedFolder(row, folder), mails)) return false;
        return PerformMailDrop(folder, mails);
    }

    private bool PerformMailDrop(IBaseFolderMenuItem folder, IReadOnlyList<MailCopy> mails)
    {
        if (_menus?.Mail is not { } mail) return false;
        // Only the mails of the folder's accounts move (Windows: other accounts' mails stay put).
        var moving = MailDragPayload.MailsFor(folder, mails);
        if (moving.Count == 0) return false;
        _ = MoveAsync(mail, moving, folder);
        return true;
    }

    private async Task MoveAsync(IMailShellClient mail, List<MailCopy> mails, IBaseFolderMenuItem folder)
    {
        try { await mail.PerformMoveOperationAsync(mails, folder); }
        catch (Exception exception) { _error(exception); }
    }

    #region Contacts

    /// <summary>The contact filter row under <paramref name="row"/> when it accepts <paramref name="properties"/>.</summary>
    private ContactFilterViewModel? ContactListAtRow(nint row, IReadOnlyDictionary<string, object> properties)
    {
        if (row < 0 || row >= _outline.RowCount || _outline.ItemAtRow(row) is not Node node) return null;
        return node.Item is ContactFilterViewModel filter && ShellPaneRows.IsEnabled(filter) && filter.CanAccept(properties) ? filter : null;
    }

    private NSDragOperation ValidateContactDrop(INSDraggingInfo info)
    {
        var ids = ContactDragPayload.From(info);
        var properties = ContactDragPayload.DataProperties(ids);
        var row = _outline.GetRow(_outline.ConvertPointFromView(info.DraggingLocation, null));
        if (ids.Count == 0 || ContactListAtRow(row, properties) is not { } list)
        {
            SetDropRow(-1);
            UpdateContactDragCaption(info, ids.Count, null);
            return NSDragOperation.None;
        }

        if (_outline.ItemAtRow(row) is { } node) _outline.SetDropItem(node, DropOnItem);
        SetDropRow(row);
        UpdateContactDragCaption(info, ids.Count, list.GetDropCaption(properties));
        return NSDragOperation.Copy;
    }

    private bool AcceptContactDrop(INSDraggingInfo info)
    {
        var ids = ContactDragPayload.From(info);
        var properties = ContactDragPayload.DataProperties(ids);
        var row = _outline.GetRow(_outline.ConvertPointFromView(info.DraggingLocation, null));
        SetDropRow(-1);
        if (ids.Count == 0 || ContactListAtRow(row, properties) is not { } list) return false;
        _ = DropContactsAsync(list, properties);
        return true;
    }

    private async Task DropContactsAsync(ContactFilterViewModel list, IReadOnlyDictionary<string, object> properties)
    {
        try { await list.HandleDropAsync(properties); }
        catch (Exception exception) { _error(exception); }
    }

    private void UpdateContactDragCaption(INSDraggingInfo info, int count, string? caption)
    {
        if (!CaptionChanged(info, count, caption)) return;
        var image = ContactDragPayload.CreateImage(count, caption);
        info.EnumerateDraggingItems(NSDraggingItemEnumerationOptions.Concurrent, _outline, ContactDragPayload.ItemClasses().Handle, new NSDictionary(),
            (NSDraggingItem item, nint index, ref bool stop) =>
            {
                if (index != 0) return;
                var frame = item.DraggingFrame;
                item.SetDraggingFrame(new CoreGraphics.CGRect(frame.Location, image.Size), image);
            });
    }

    #endregion

    /// <summary>
    /// Records the caption the drag image shows; false when it already shows it. The caption state is owned
    /// here (not reset with the drop highlight), so leaving a valid row for an invalid one redraws the plain
    /// card. A new drag session starts from the source's plain card, so its first caption always draws.
    /// </summary>
    private bool CaptionChanged(INSDraggingInfo info, int count, string? caption)
    {
        if (count == 0) return false;
        var sequence = info.DraggingSequenceNumber;
        if (sequence != _dropSequence)
        {
            _dropSequence = sequence;
            _dropCaption = null;
        }
        if (caption == _dropCaption) return false;
        _dropCaption = caption;
        return true;
    }

    private void SetDropRow(nint row)
    {
        if (row == _dropRow) return;
        if (_dropRow >= 0 && _dropRow < _outline.RowCount && _outline.GetRowView(_dropRow, false) is WinoShellRowView previous) previous.IsDropTarget = false;
        _dropRow = row;
        if (row >= 0 && _outline.GetRowView(row, false) is WinoShellRowView current) current.IsDropTarget = true;
    }

    /// <summary>Redraws the dragged card with the caption chip while it is over the pane (Windows DragUIOverride.Caption).</summary>
    private void UpdateDragCaption(INSDraggingInfo info, int count, string? caption, bool refused)
    {
        if (!CaptionChanged(info, count, caption)) return;
        var image = MailDragPayload.CreateImage(count, caption, refused);
        info.EnumerateDraggingItems(NSDraggingItemEnumerationOptions.Concurrent, _outline, MailDragPayload.ItemClasses().Handle, new NSDictionary(),
            (NSDraggingItem item, nint index, ref bool stop) =>
            {
                if (index != 0) return;
                var frame = item.DraggingFrame;
                item.SetDraggingFrame(new CoreGraphics.CGRect(frame.Location, image.Size), image);
            });
    }

#if DEBUG
    /// <summary>
    /// Debug bridge, using the mail list selection as the dragged mails: "drag-validate ROW" reports
    /// whether they can drop on a pane row, "drag-drop ROW" performs that drop, "drag-highlight ROW|off"
    /// shows the drop highlight on a row for a screenshot.
    /// </summary>
    private void RegisterDropDebugCommands()
    {
        Infrastructure.MacDebugBridge.Register("drag-validate", args =>
        {
            var (row, folder, mails, error) = DebugDropTarget(args);
            if (error is not null) return Task.FromResult(error);
            bool valid = MailDragPayload.CanDropOn(folder!, IsSelectedFolder(row, folder!), mails);
            return Task.FromResult($"row={row} folder='{folder!.FolderName}' moveTarget={folder.IsMoveTarget} selected={IsSelectedFolder(row, folder)} " +
                $"mails={mails.Count} matching={MailDragPayload.MailsFor(folder, mails).Count} valid={valid} caption='{(valid ? string.Format(Translator.DragMoveToFolderCaption, folder.FolderName) : Translator.DragCannotMoveHereCaption)}'");
        });
        Infrastructure.MacDebugBridge.Register("drag-drop", args =>
        {
            var (row, folder, mails, error) = DebugDropTarget(args);
            if (error is not null) return Task.FromResult(error);
            if (!MailDragPayload.CanDropOn(folder!, IsSelectedFolder(row, folder!), mails)) return Task.FromResult("refused: not a valid drop target");
            return Task.FromResult(PerformMailDrop(folder!, mails) ? $"moving {MailDragPayload.MailsFor(folder!, mails).Count} to '{folder!.FolderName}'" : "no mail client");
        });
        Infrastructure.MacDebugBridge.Register("contacts-drag-validate", args =>
        {
            if (args.Length == 0 || !int.TryParse(args[0], out var index)) return Task.FromResult("usage: ROW");
            var ids = ContactDragPayload.DebugSelection?.Invoke() ?? [];
            if (ids.Count == 0) return Task.FromResult("no contacts selected");
            var properties = ContactDragPayload.DataProperties(ids);
            var list = ContactListAtRow(index, properties);
            var item = index >= 0 && index < _outline.RowCount && _outline.ItemAtRow(index) is Node node ? node.Item : null;
            return Task.FromResult($"row={index} item={item?.GetType().Name} contacts={ids.Count} valid={list is not null} caption='{list?.GetDropCaption(properties)}'");
        });
        Infrastructure.MacDebugBridge.Register("contacts-drag-drop", args =>
        {
            if (args.Length == 0 || !int.TryParse(args[0], out var index)) return Task.FromResult("usage: ROW");
            var ids = ContactDragPayload.DebugSelection?.Invoke() ?? [];
            if (ids.Count == 0) return Task.FromResult("no contacts selected");
            var properties = ContactDragPayload.DataProperties(ids);
            if (ContactListAtRow(index, properties) is not { } list) return Task.FromResult("refused: not a contact list row");
            _ = DropContactsAsync(list, properties);
            return Task.FromResult($"adding {ids.Count} to '{list.Name}'");
        });
        Infrastructure.MacDebugBridge.Register("drag-highlight", args =>
        {
            if (args.Length == 0 || args[0].Equals("off", StringComparison.OrdinalIgnoreCase)) { SetDropRow(-1); return Task.FromResult("ok"); }
            SetDropRow(int.Parse(args[0]));
            return Task.FromResult("ok");
        });
    }

    private (nint Row, IBaseFolderMenuItem? Folder, IReadOnlyList<MailCopy> Mails, string? Error) DebugDropTarget(string[] args)
    {
        if (args.Length == 0 || !int.TryParse(args[0], out var index)) return (-1, null, [], "usage: ROW");
        var (row, folder) = FolderAtRow(index);
        if (folder is null) return (-1, null, [], $"row {index} is not a folder row");
        var mails = MailDragPayload.DebugSelection?.Invoke() ?? [];
        return mails.Count == 0 ? (row, folder, mails, "no mails selected in the list") : (row, folder, mails, null);
    }
#endif
}
