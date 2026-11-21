using Wino.Mail.ViewModels;

namespace Wino.Mail.Linux.Views.Mail;

internal sealed partial class MailReaderView
{
    private readonly Gtk.FlowBox _attachmentChips = Gtk.FlowBox.New();

    partial void BuildAttachments()
    {
        _attachmentChips.SetSelectionMode(Gtk.SelectionMode.None);
        _attachmentChips.SetMaxChildrenPerLine(8);
        _attachmentChips.SetVisible(false);
        _attachmentSlot.SetChild(_attachmentChips);
    }

    partial void InitializeAttachments()
    {
        Bind.BindCollection(ViewModel.Attachments, UpdateAttachments);
        Bind.Bind(nameof(MailRenderingPageViewModel.CurrentRenderModel), static vm => vm.CurrentRenderModel, _ => UpdateAttachments());
    }

    private void UpdateAttachments()
    {
        _attachmentChips.RemoveAll();
        foreach (var attachment in ViewModel.Attachments)
        {
            var chip = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
            chip.AddCssClass("wino-attachment");
            chip.Append(Gtk.Label.New($"{attachment.FileName}  ({attachment.ReadableSize})"));
            _attachmentChips.Append(chip);
        }
        _attachmentChips.SetVisible(ViewModel.Attachments.Count > 0);
    }
}
