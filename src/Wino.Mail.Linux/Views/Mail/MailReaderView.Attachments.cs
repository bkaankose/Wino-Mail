using Wino.Mail.ViewModels;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>The attachment strip under the body; every attachment of the message (<c>ViewModel.Attachments</c>), as on macOS.</summary>
internal sealed partial class MailReaderView
{
    private AttachmentStrip? _attachments;

    partial void BuildAttachments()
    {
        var strip = _attachments = new AttachmentStrip();
        _attachmentSlot.SetChild(strip.Root);
        Bindings.Own(strip);
    }

    partial void InitializeAttachments()
    {
        if (_attachments is not { } strip) return;
        strip.OpenRequested += attachment => Observe(ViewModel.OpenAttachmentCommand.ExecuteAsync(attachment));
        strip.SaveRequested += attachment => Observe(ViewModel.SaveAttachmentCommand.ExecuteAsync(attachment));
        strip.SaveAllRequested += () => Observe(ViewModel.SaveAllAttachmentsCommand.ExecuteAsync(null));
        Bind.BindCollection(ViewModel.Attachments, UpdateAttachments);
        Bind.Bind(nameof(MailRenderingPageViewModel.CurrentRenderModel), static vm => vm.CurrentRenderModel, _ => UpdateAttachments());
        WhenWidthChanged((_, state) => strip.SetCompact(state == ReaderWidthState.Compact));
#if DEBUG
        WhenDescribing(strip.Describe);
#endif
    }

    private void UpdateAttachments()
    {
        if (!IsReleased) _attachments?.SetAttachments([.. ViewModel.Attachments]);
    }

#if DEBUG
    internal AttachmentStrip? DebugAttachments => _attachments;
    internal void DebugOpenAttachment(Wino.Mail.ViewModels.Data.MailAttachmentViewModel attachment) => Observe(ViewModel.OpenAttachmentCommand.ExecuteAsync(attachment));
    internal void DebugSaveAttachment(Wino.Mail.ViewModels.Data.MailAttachmentViewModel attachment) => Observe(ViewModel.SaveAttachmentCommand.ExecuteAsync(attachment));
    internal void DebugSaveAll() => Observe(ViewModel.SaveAllAttachmentsCommand.ExecuteAsync(null));
#endif
}
