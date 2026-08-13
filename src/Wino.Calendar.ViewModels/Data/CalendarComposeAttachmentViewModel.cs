using System;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Extensions;

namespace Wino.Calendar.ViewModels.Data;

public class CalendarComposeAttachmentViewModel
{
    public Guid Id { get; } = Guid.NewGuid();
    public string FileName { get; }
    public string FilePath { get; }
    public string FileExtension { get; }
    public long Size { get; }
    public string ReadableSize => Size.GetBytesReadable();
    public MailAttachmentType AttachmentType { get; }

    public CalendarComposeAttachmentViewModel(string fileName, string filePath, string fileExtension, long size)
    {
        FileName = fileName;
        FilePath = filePath;
        FileExtension = fileExtension;
        Size = size;
        AttachmentType = GetAttachmentType(fileExtension);
    }

    public CalendarEventComposeAttachmentDraft ToDraftModel()
    {
        return new CalendarEventComposeAttachmentDraft
        {
            Id = Id,
            FileName = FileName,
            FilePath = FilePath,
            FileExtension = FileExtension,
            Size = Size
        };
    }

    private static MailAttachmentType GetAttachmentType(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return MailAttachmentType.None;

        return extension.ToLowerInvariant() switch
        {
            ".exe" => MailAttachmentType.Executable,
            ".rar" => MailAttachmentType.RarArchive,
            ".zip" => MailAttachmentType.Archive,
            ".ogg" or ".mp3" or ".wav" or ".aac" or ".alac" => MailAttachmentType.Audio,
            ".mp4" or ".wmv" or ".avi" or ".flv" => MailAttachmentType.Video,
            ".pdf" => MailAttachmentType.PDF,
            ".htm" or ".html" => MailAttachmentType.HTML,
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".jiff" => MailAttachmentType.Image,
            _ => MailAttachmentType.Other
        };
    }
}
