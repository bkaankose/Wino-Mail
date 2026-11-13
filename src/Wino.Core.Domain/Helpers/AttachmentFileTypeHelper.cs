#nullable enable
using System.IO;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Helpers;

public static class AttachmentFileTypeHelper
{
    private const int MaxExtensionLabelLength = 4;
    private const uint NeutralAccentColor = 0x6E6E73;

    public static MailAttachmentType GetAttachmentType(string? fileNameOrExtension)
    {
        var extension = GetExtension(fileNameOrExtension);
        if (extension.Length == 0)
            return MailAttachmentType.None;

        return extension switch
        {
            ".exe" or ".com" or ".msi" or ".msix" or ".appx" or ".bat" or ".cmd" or ".scr" or ".ps1" or ".vbs" or ".jar" or ".apk" or ".dmg" or ".pkg" => MailAttachmentType.Executable,
            ".rar" => MailAttachmentType.RarArchive,
            ".zip" or ".7z" or ".tar" or ".gz" or ".tgz" or ".bz2" or ".xz" or ".cab" or ".iso" => MailAttachmentType.Archive,
            ".ogg" or ".oga" or ".opus" or ".mp3" or ".m4a" or ".wav" or ".wma" or ".aac" or ".alac" or ".flac" or ".aif" or ".aiff" or ".amr" or ".mid" or ".midi" or ".weba" => MailAttachmentType.Audio,
            ".mp4" or ".m4v" or ".mov" or ".wmv" or ".avi" or ".flv" or ".mkv" or ".webm" or ".mpg" or ".mpeg" or ".3gp" or ".3g2" or ".ogv" => MailAttachmentType.Video,
            ".pdf" => MailAttachmentType.PDF,
            ".htm" or ".html" or ".xhtml" or ".mht" or ".mhtml" => MailAttachmentType.HTML,
            ".png" or ".jpg" or ".jpeg" or ".jfif" or ".gif" or ".webp" or ".bmp" or ".tif" or ".tiff" or ".heic" or ".heif" or ".avif" or ".svg" or ".ico" => MailAttachmentType.Image,
            _ => MailAttachmentType.Other
        };
    }

    /// <summary>The upper-case extension shown on type badges and in attachment details, at most four characters.</summary>
    public static string GetExtensionLabel(string? fileNameOrExtension)
    {
        var extension = GetExtension(fileNameOrExtension).TrimStart('.').ToUpperInvariant();
        return extension.Length > MaxExtensionLabelLength ? extension[..MaxExtensionLabelLength] : extension;
    }

    /// <summary>The badge color for a file as 0xRRGGBB, shared so every platform colors a type the same way.</summary>
    public static uint GetAccentColor(string? fileNameOrExtension)
    {
        var extension = GetExtension(fileNameOrExtension);

        switch (extension)
        {
            case ".doc" or ".docx" or ".docm" or ".odt" or ".rtf":
                return 0x2B579A;
            case ".xls" or ".xlsx" or ".xlsm" or ".ods" or ".csv":
                return 0x217346;
            case ".ppt" or ".pptx" or ".pptm" or ".odp":
                return 0xB7472A;
        }

        return GetAttachmentType(extension) switch
        {
            MailAttachmentType.PDF => 0xD13438,
            MailAttachmentType.Audio => 0x8764B8,
            MailAttachmentType.Video => 0xC239B3,
            MailAttachmentType.Image => 0x0078D4,
            MailAttachmentType.Archive or MailAttachmentType.RarArchive => 0x795548,
            MailAttachmentType.HTML => 0xCA5010,
            MailAttachmentType.Executable => 0x4A5459,
            _ => NeutralAccentColor
        };
    }

    private static string GetExtension(string? fileNameOrExtension)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrExtension))
            return string.Empty;

        var extension = fileNameOrExtension.StartsWith('.')
            ? fileNameOrExtension
            : Path.GetExtension(fileNameOrExtension);

        return extension.ToLowerInvariant();
    }
}
