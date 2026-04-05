using System;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Extensions;

namespace Wino.Calendar.ViewModels.Data;

public partial class CalendarAttachmentViewModel : ObservableObject
{
    public CalendarAttachment Attachment { get; }

    public Guid Id => Attachment.Id;
    public string FileName => Attachment.FileName;
    public string ReadableSize { get; }
    public MailAttachmentType AttachmentType { get; }
    public bool IsDownloaded => Attachment.IsDownloaded;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public CalendarAttachmentViewModel(CalendarAttachment attachment)
    {
        Attachment = attachment;
        ReadableSize = attachment.Size.GetBytesReadable();

        var extension = Path.GetExtension(FileName);
        AttachmentType = GetAttachmentType(extension);
    }

    private MailAttachmentType GetAttachmentType(string extension)
    {
        if (string.IsNullOrEmpty(extension))
            return MailAttachmentType.None;

        switch (extension.ToLower())
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
