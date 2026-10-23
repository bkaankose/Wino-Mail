#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Models.Attachments;
using MailCal.Core.Domain.Models.Calendar;
using MailCal.Core.Extensions;
using MailCal.Core.ViewModels.Attachments;

namespace MailCal.Calendar.ViewModels.Data;

public partial class CalendarComposeAttachmentViewModel : AttachmentViewModelBase
{
    public Guid Id { get; } = Guid.NewGuid();
    public override string FileName { get; }
    public string FilePath { get; }
    public string FileExtension { get; }
    public long Size { get; }
    public string ReadableSize => Size.GetBytesReadable();

    public CalendarComposeAttachmentViewModel(string fileName, string filePath, string fileExtension, long size)
    {
        FileName = fileName;
        FilePath = filePath;
        FileExtension = fileExtension;
        Size = size;
    }

    public CalendarEventComposeAttachmentDraft ToDraftModel() => new()
    {
        Id = Id,
        FileName = FileName,
        FilePath = FilePath,
        FileExtension = FileExtension,
        Size = Size
    };

    public AttachmentFileSource CreateFileSource() =>
        new(FileName, null, AttachmentFileOrigin.Local, OpenReadAsync, FilePath);

    private ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<Stream>(File.OpenRead(FilePath));
    }
}
