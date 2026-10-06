#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Attachments;
using Wino.Core.Extensions;
using Wino.Core.ViewModels.Attachments;

namespace Wino.Calendar.ViewModels.Data;

public partial class CalendarAttachmentViewModel : AttachmentViewModelBase
{
    public CalendarAttachment Attachment { get; }
    public Guid Id => Attachment.Id;
    public override string FileName => Attachment.FileName;
    public string ReadableSize { get; }
    public bool IsDownloaded => Attachment.IsDownloaded;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public CalendarAttachmentViewModel(CalendarAttachment attachment)
    {
        Attachment = attachment;
        ReadableSize = attachment.Size.GetBytesReadable();
    }

    public AttachmentFileSource CreateFileSource() =>
        new(FileName, Attachment.ContentType, AttachmentFileOrigin.Received, OpenReadAsync, Attachment.LocalFilePath);

    private ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(Attachment.LocalFilePath))
            throw new FileNotFoundException("The calendar attachment has not been downloaded.");

        return ValueTask.FromResult<Stream>(File.OpenRead(Attachment.LocalFilePath));
    }
}
