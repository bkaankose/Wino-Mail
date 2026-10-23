using System;

namespace MailCal.Core.Domain.Models.Personalization;

public static class ThemeWallpaperPreviewPath
{
    public static Uri? GetAbsoluteUri(string? path)
        => Uri.TryCreate(path, UriKind.Absolute, out var uri) ? uri : null;
}
