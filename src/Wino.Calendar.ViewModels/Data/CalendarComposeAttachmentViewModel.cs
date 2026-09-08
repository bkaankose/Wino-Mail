#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Attachments;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Extensions;
using Wino.Core.ViewModels.Attachments;

namespace Wino.Calendar.ViewModels.Data;

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
