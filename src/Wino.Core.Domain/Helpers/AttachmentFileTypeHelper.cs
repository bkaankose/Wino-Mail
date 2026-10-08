#nullable enable
using System.IO;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Helpers;

public static class AttachmentFileTypeHelper
{
    public static MailAttachmentType GetAttachmentType(string? fileNameOrExtension)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrExtension))
            return MailAttachmentType.None;

        var extension = fileNameOrExtension.StartsWith('.')
            ? fileNameOrExtension
            : Path.GetExtension(fileNameOrExtension);

        return extension.ToLowerInvariant() switch
        {
            ".exe" or ".com" or ".msi" or ".bat" or ".cmd" or ".scr" => MailAttachmentType.Executable,
            ".rar" => MailAttachmentType.RarArchive,
            ".zip" or ".7z" or ".tar" or ".gz" or ".bz2" => MailAttachmentType.Archive,
            ".ogg" or ".mp3" or ".wav" or ".aac" or ".alac" or ".flac" => MailAttachmentType.Audio,
            ".mp4" or ".wmv" or ".avi" or ".flv" or ".mkv" or ".webm" => MailAttachmentType.Video,
            ".pdf" => MailAttachmentType.PDF,
            ".htm" or ".html" or ".mht" => MailAttachmentType.HTML,
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".jiff" or ".webp" or ".bmp" or ".tif" or ".tiff" => MailAttachmentType.Image,
            _ => MailAttachmentType.Other
        };
    }
}
