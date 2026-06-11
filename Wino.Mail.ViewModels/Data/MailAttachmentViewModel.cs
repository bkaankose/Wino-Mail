using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Models.Common;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Reader;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// Attachment of a rendered or composed message. Content stays out of the UI process:
/// rendered attachments are extracted to a file by the companion on demand (tracked by
/// <see cref="AttachmentIndex"/>); composer attachments are plain file paths.
/// </summary>
public partial class MailAttachmentViewModel : ObservableObject
{
    /// <summary>Index in the companion's render model; -1 for composer-added files.</summary>
    public int AttachmentIndex { get; } = -1;

    public MailAttachmentType AttachmentType { get; }
    public string FileName { get; }
    public string FilePath { get; set; }
    public string ReadableSize { get; }
    public long Size { get; }

    /// <summary>
    /// In-memory content for share-target files that have no backing path yet.
    /// </summary>
    public byte[] Content { get; set; }

    /// <summary>
    /// Gets or sets whether attachment is busy with opening or saving etc.
    /// </summary>
    [ObservableProperty]
    private bool isBusy;

    /// <summary>Rendered message attachment; content extracted over RPC on demand.</summary>
    public MailAttachmentViewModel(MailAttachmentInfo attachmentInfo)
    {
        AttachmentIndex = attachmentInfo.AttachmentIndex;
        FileName = attachmentInfo.FileName;
        Size = attachmentInfo.Size;
        ReadableSize = attachmentInfo.Size.GetBytesReadable();
        AttachmentType = GetAttachmentType(Path.GetExtension(FileName));
    }

    /// <summary>Existing draft attachment, already extracted to a file by the companion.</summary>
    public MailAttachmentViewModel(DraftAttachmentInfo draftAttachmentInfo)
    {
        FileName = draftAttachmentInfo.FileName;
        FilePath = draftAttachmentInfo.FilePath;
        Size = draftAttachmentInfo.Size;
        ReadableSize = draftAttachmentInfo.Size.GetBytesReadable();
        AttachmentType = GetAttachmentType(Path.GetExtension(FileName));
    }

    /// <summary>File attached in the composer from a user-picked path.</summary>
    public MailAttachmentViewModel(string filePath, long size)
    {
        FileName = Path.GetFileName(filePath);
        FilePath = filePath;
        Size = size;
        ReadableSize = size.GetBytesReadable();
        AttachmentType = GetAttachmentType(Path.GetExtension(FileName));
    }

    public MailAttachmentViewModel(SharedFile sharedFile)
    {
        Content = sharedFile.Data;

        FileName = sharedFile.FileName;
        FilePath = sharedFile.FullFilePath;
        Size = sharedFile.Data?.LongLength ?? 0;

        ReadableSize = Size.GetBytesReadable();

        var extension = Path.GetExtension(FileName);
        AttachmentType = GetAttachmentType(extension);
    }

    public MailAttachmentType GetAttachmentType(string mediaSubtype)
    {
        if (string.IsNullOrEmpty(mediaSubtype))
            return MailAttachmentType.None;

        switch (mediaSubtype.ToLower())
        {
            case ".exe":
                return MailAttachmentType.Executable;
            case ".rar":
                return MailAttachmentType.RarArchive;
            case ".zip":
                return MailAttachmentType.Archive;
            case ".ogg":
            case ".mp3":
            case ".wav":
            case ".aac":
            case ".alac":
                return MailAttachmentType.Audio;
            case ".mp4":
            case ".wmv":
            case ".avi":
            case ".flv":
                return MailAttachmentType.Video;
            case ".pdf":
                return MailAttachmentType.PDF;
            case ".htm":
            case ".html":
                return MailAttachmentType.HTML;
            case ".png":
            case ".jpg":
            case ".jpeg":
            case ".gif":
            case ".jiff":
                return MailAttachmentType.Image;
            default:
                return MailAttachmentType.Other;
        }
    }
}
