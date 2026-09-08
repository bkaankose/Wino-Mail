#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using MimeKit;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Attachments;
using Wino.Core.Domain.Models.Common;
using Wino.Core.Extensions;
using Wino.Core.ViewModels.Attachments;

namespace Wino.Mail.ViewModels.Data;

public partial class MailAttachmentViewModel : AttachmentViewModelBase
{
    private readonly MimePart? _mimePart;

    public override string FileName { get; }
    public string FilePath { get; set; } = string.Empty;
    public string ReadableSize { get; }
    public byte[] Content { get; set; }
    public IMimeContent? MimeContent => _mimePart?.Content;
    public string? DeclaredMimeType => _mimePart?.ContentType?.MimeType;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public MailAttachmentViewModel(MimePart mimePart)
    {
        ArgumentNullException.ThrowIfNull(mimePart);
        _mimePart = mimePart;

        using var memoryStream = new MemoryStream();
        mimePart.Content?.DecodeTo(memoryStream);

        Content = memoryStream.ToArray();
        FileName = string.IsNullOrWhiteSpace(mimePart.FileName) ? "attachment.bin" : mimePart.FileName;
        ReadableSize = ((long)Content.Length).GetBytesReadable();
    }

    public MailAttachmentViewModel(SharedFile sharedFile)
    {
        ArgumentNullException.ThrowIfNull(sharedFile);
        Content = sharedFile.Data ?? [];
        FileName = string.IsNullOrWhiteSpace(sharedFile.FileName) ? "attachment.bin" : sharedFile.FileName;
        FilePath = sharedFile.FullFilePath ?? string.Empty;
        ReadableSize = ((long)Content.Length).GetBytesReadable();
    }

    public AttachmentFileSource CreateFileSource(AttachmentFileOrigin origin) =>
        new(FileName, DeclaredMimeType, origin, OpenReadAsync, FilePath);

    private ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<Stream>(new MemoryStream(Content, writable: false));
    }
}
