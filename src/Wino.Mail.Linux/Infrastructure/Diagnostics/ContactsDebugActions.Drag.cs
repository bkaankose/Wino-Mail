#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.Linux.Views.Contacts;
using Wino.Mail.Linux.Views.Mail;
using Wino.Mail.ViewModels.Activation;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// Drag and file verbs: drag-validate row (a payload from the selection, validated on that sidebar row; the drag stays
/// active until drag-end), drag-drop row, drag-drop-text row text, drag-end, drag-highlight row|off, drag-card count
/// caption (window "Contact drag card"), open-vcf path (the activation path GApplication's open signal takes).
/// </summary>
internal static partial class ContactsDebugActions
{
    private static Gtk.Window? _cardWindow;

    static partial void AddDragVerbs()
    {
        Verb("drag-validate", args =>
        {
            var token = BeginSelectionDrag();
            var sidebar = Shell.Sidebar;
            var verdict = sidebar.DebugValidateDrop(SidebarDebugActions.Resolve(sidebar, string.Join(' ', args)));
            return $"action={verdict.Action} caption='{verdict.Caption}' refused={verdict.Refused} dragging={ContactDragPayload.Current.Count} token={token.Length > 0}";
        });
        Verb("drag-drop", args =>
        {
            var token = BeginSelectionDrag();
            var sidebar = Shell.Sidebar;
            var taken = sidebar.DebugDrop(SidebarDebugActions.Resolve(sidebar, string.Join(' ', args)), token);
            ContactDragPayload.End();
            return $"taken={taken} dragging={ContactDragPayload.Current.Count}";
        });
        Verb("drag-drop-text", args =>
        {
            if (args.Length < 2) throw new ArgumentException("drag-drop-text needs a row and a text");
            BeginSelectionDrag();
            var sidebar = Shell.Sidebar;
            var taken = sidebar.DebugDrop(SidebarDebugActions.Resolve(sidebar, args[0]), string.Join(' ', args[1..]));
            ContactDragPayload.End();
            return $"taken={taken}";
        });
        Verb("drag-end", _ =>
        {
            ContactDragPayload.End();
            Shell.Sidebar.DebugHighlight(null);
            return "ended";
        });
        Verb("drag-highlight", args =>
        {
            var sidebar = Shell.Sidebar;
            var row = string.Join(' ', args);
            sidebar.DebugHighlight(row == "off" ? null : SidebarDebugActions.Resolve(sidebar, row));
            return "highlight " + row;
        });
        Verb("drag-card", args =>
        {
            var page = Page;
            var count = args.Length > 0 ? int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture) : 1;
            var contact = page.ItemAtPosition(0) ?? throw new ArgumentException("no rows");
            var cell = page.Cells.CellOf(contact) ?? throw new ArgumentException("first row not bound");
            var card = page.CreateDragCard(cell, count);
            card.SetCaption(args.Length > 1 ? string.Join(' ', args[1..]) : null, false);
            _cardWindow?.Destroy();
            var window = Gtk.Window.New();
            window.SetTitle("Contact drag card");
            window.SetDefaultSize(MailDragCard.CardWidth + 40, MailDragCard.CardHeight + 60);
            var frame = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
            frame.SetMarginTop(20);
            frame.SetMarginStart(20);
            frame.Append(card.Root);
            window.SetChild(frame);
            window.Present();
            _cardWindow = window;
            return "card shown";
        });
        Verb("drag-card-close", _ =>
        {
            _cardWindow?.Destroy();
            _cardWindow = null;
            return "closed";
        });
        Verb("open-vcf", async args =>
        {
            var path = string.Join(' ', args);
            var item = LinuxActivation.Classify(Gio.FileHelper.NewForPath(path)) ?? throw new ArgumentException("not an activation");
            await Services.GetRequiredService<ActivationRouter>().HandleAsync([item]);
            return "kind=" + item.Kind;
        });
    }

    private static string BeginSelectionDrag()
    {
        var page = Page;
        var contact = page.ViewModel.SelectedContact ?? throw new ArgumentException("nothing selected");
        return page.BeginDrag(contact) ?? throw new ArgumentException("drag refused");
    }
}
#endif
