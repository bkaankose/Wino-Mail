using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.MenuItems.Contracts;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Shell;

/// <summary>One sidebar row from the shared row description. The cell only renders; the shell handles the click.</summary>
internal static class ShellMenuRow
{
    public static Gtk.ListBoxRow Create(IMenuItem item, ShellMenuRowContent content)
    {
        var row = Gtk.ListBoxRow.New();
        var box = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
        box.SetMarginStart(4 + content.Indent * 16);
        box.SetMarginEnd(4);
        row.SetChild(box);

        if (content.Kind == ShellMenuRowKind.Header)
        {
            row.SetActivatable(false);
            row.SetSelectable(false);
            var header = Label(content.Title);
            header.AddCssClass("heading");
            header.AddCssClass("dim-label");
            box.Append(header);
            return row;
        }

        row.SetSensitive(content.IsEnabled);
        if (content.Kind == ShellMenuRowKind.Account)
        {
            box.Append(Adw.Avatar.New(28, content.Title, true));
            var text = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
            text.SetHexpand(true);
            text.SetValign(Gtk.Align.Center);
            var title = Label(content.Title);
            title.AddCssClass("heading");
            text.Append(title);
            if (!string.IsNullOrWhiteSpace(content.Subtitle))
            {
                var subtitle = Label(content.Subtitle);
                subtitle.AddCssClass("caption");
                subtitle.AddCssClass("dim-label");
                text.Append(subtitle);
            }
            box.Append(text);
            if (content.ShowsSync) box.Append(Adw.Spinner.New());
        }
        else
        {
            box.Append(new WinoIcon(content.Glyph == WinoIconGlyph.None ? WinoIconGlyph.Folder : content.Glyph).Root);
            var title = Label(content.Title);
            title.SetHexpand(true);
            box.Append(title);
        }

        if (content.UnreadCount > 0)
        {
            var unread = Gtk.Label.New(content.UnreadCount.ToString(System.Globalization.CultureInfo.CurrentCulture));
            unread.AddCssClass("wino-unread-badge");
            unread.AddCssClass("numeric");
            box.Append(unread);
        }

        if (content.IsExpanded is { } expanded && content.Kind != ShellMenuRowKind.Account)
        {
            var toggle = Gtk.Button.NewFromIconName(expanded ? "pan-down-symbolic" : "pan-end-symbolic");
            toggle.SetHasFrame(false);
            toggle.SetValign(Gtk.Align.Center);
            toggle.OnClicked += (_, _) => item.IsExpanded = !item.IsExpanded;
            box.Append(toggle);
        }

        return row;
    }

    private static Gtk.Label Label(string? text)
    {
        var label = Gtk.Label.New(text ?? string.Empty);
        label.SetXalign(0);
        label.SetEllipsize(Pango.EllipsizeMode.End);
        return label;
    }
}
