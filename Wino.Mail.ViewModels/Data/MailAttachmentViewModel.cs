using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using MimeKit;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Common;
using Wino.Core.Extensions;

namespace Wino.Mail.ViewModels.Data;

public partial class MailAttachmentViewModel : ObservableObject
{
    private readonly MimePart _mimePart;

    public MailAttachmentType AttachmentType { get; }
    public string FileName { get; }
    public string FilePath { get; set; }
    public string ReadableSize { get; }
    public byte[] Content { get; set; }

    public IMimeContent MimeContent => _mimePart.Content;

    /// <summary>
    /// Gets or sets whether attachment is busy with opening or saving etc.
    /// </summary>
    [ObservableProperty]
    private bool isBusy;

    public MailAttachmentViewModel(MimePart mimePart)
    {
        _mimePart = mimePart;

        var memoryStream = new MemoryStream();

        using (memoryStream) mimePart.Content.DecodeTo(memoryStream);

        Content = memoryStream.ToArray();

        FileName = mimePart.FileName;
        ReadableSize = ((long)Content.Length).GetBytesReadable();

        var extension = Path.GetExtension(FileName);
        AttachmentType = GetAttachmentType(extension);
    }

    public MailAttachmentViewModel(SharedFile sharedFile)
    {
        Content = sharedFile.Data;

        FileName = sharedFile.FileName;
        FilePath = sharedFile.FullFilePath;

        ReadableSize = ((long)sharedFile.Data.Length).GetBytesReadable();

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
