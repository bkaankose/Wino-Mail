using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Mail.Linux.Views.Dialogs;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>
/// The folder picker in a popover under a Move button (macOS <c>MoveFolderPopover</c>): the reader command bar and
/// the multi-selection pane use it. The source folders cannot be picked; a pick closes the popover, then runs.
/// </summary>
internal sealed class MoveFolderPopover : IDisposable
{
    private readonly FolderPicker _picker;
    private readonly Gtk.Popover _popover;
    private readonly Action<IMailItemFolder> _picked;
    private IDisposable? _closed;
    private bool _parented;
    private bool _disposed;

    public MoveFolderPopover(IReadOnlyList<IMailItemFolder?> folders, IReadOnlySet<Guid> sourceFolderIds, Action<IMailItemFolder> picked)
    {
        WinoLiveObjects.Track(this);
        _picked = picked;
        _picker = new FolderPicker(folders, sourceFolderIds);
        _picker.Root.SetSizeRequest(300, -1);
        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        box.SetMarginTop(12);
        box.SetMarginBottom(12);
        box.SetMarginStart(12);
        box.SetMarginEnd(12);
        var heading = Gtk.Label.New(Translator.MailOperation_Move);
        heading.AddCssClass("heading");
        heading.SetXalign(0);
        box.Append(heading);
        box.Append(_picker.Root);
        _popover = Gtk.Popover.New();
        _popover.SetPosition(Gtk.PositionType.Bottom);
        _popover.SetAutohide(true);
        _popover.SetChild(box);
        _picker.Picked += folder =>
        {
            Close();
            MainLoop.Post(() => _picked(folder));
        };
    }

    public bool IsShown => _popover.GetVisible();

    public void Show(Gtk.Widget anchor)
    {
        if (_disposed || _parented) return;
        _parented = true;
        _popover.SetParent(anchor);
        var popover = _popover;
        _closed = GtkSignals.Connect<Gtk.Popover>(h => popover.OnClosed += h, h => popover.OnClosed -= h, (_, _) => Teardown());
        _popover.Popup();
        _picker.FocusSearch();
    }

    public void Close()
    {
        if (_parented) _popover.Popdown();
    }

    public void Dispose()
    {
        Close();
        Teardown();
    }

    /// <summary>
    /// The Move command of a button: the popover when the items belong to one account and the anchor is in a window,
    /// otherwise the shared path, which asks through <c>PickFolderAsync</c> or reports the problem. The caller keeps
    /// the returned popover and disposes it before asking again.
    /// </summary>
    public static Task<MoveFolderPopover?> ShowForAsync(Gtk.Widget? anchor, IReadOnlyList<MailItemViewModel> items,
        MailListPageViewModel viewModel, IFolderService folders, IDispatcher dispatcher)
        => ShowForAsync(anchor, items, viewModel.ExecuteMailOperationAsync, folders, dispatcher);

    /// <summary>The same for a caller without a list ViewModel (the reader): <paramref name="execute"/> runs the move.</summary>
    public static async Task<MoveFolderPopover?> ShowForAsync(Gtk.Widget? anchor, IReadOnlyList<MailItemViewModel> items,
        Func<MailOperationPreperationRequest, Task> execute, IFolderService folders, IDispatcher dispatcher)
    {
        if (items.Count == 0) return null;
        var mails = items.Select(static item => item.MailCopy).ToArray();
        var accounts = items.Select(static item => item.MailCopy.AssignedAccount?.Id).Distinct().ToArray();
        var rooted = false;
        await dispatcher.ExecuteOnUIThread(() => rooted = anchor?.GetRoot() is not null);
        if (accounts is not [{ } accountId] || !rooted)
        {
            await execute(new MailOperationPreperationRequest(MailOperation.Move, mails));
            return null;
        }
        var tree = await folders.GetFolderStructureForDisplayAsync(accountId);
        var sources = items.Select(static item => item.MailCopy.AssignedFolder?.Id).OfType<Guid>().ToHashSet();
        MoveFolderPopover? popover = null;
        await dispatcher.ExecuteOnUIThread(() =>
        {
            if (anchor?.GetRoot() is null) return;
            popover = new MoveFolderPopover([.. tree ?? []], sources, folder => Observe(execute(
                new MailOperationPreperationRequest(MailOperation.Move, mails, moveTargetFolder: folder))));
            popover.Show(anchor);
        });
        return popover;
    }

    private static async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { Serilog.Log.Error(exception, "Moving from the folder popover failed."); }
    }

    private void Teardown()
    {
        if (_disposed) return;
        _disposed = true;
        _closed?.Dispose();
        _closed = null;
        // GTK still uses the popover after its closed signal: it leaves its anchor (without Unparent it would stay a
        // child of it) one main-loop turn later.
        var popover = _popover;
        var parented = _parented;
        var picker = _picker;
        MainLoop.Post(() =>
        {
            if (parented) popover.Unparent();
            picker.Dispose();
        });
    }
}
