using System;
using MailCal.Core.Domain.Interfaces;

namespace MailCal.Mail.WinUI.Services;

public sealed class WindowsApplicationResourceResolver : IApplicationResourceResolver
{
    public Uri ResolvePackagedResource(string relativePath) => GetPackagedResourceUri(relativePath);

    public Uri ResolveLocalResource(string relativePath)
        => new($"ms-appdata:///local/{relativePath.Replace('\\', '/')}");

    public static Uri GetPackagedResourceUri(string relativePath)
        => new($"ms-appx:///{relativePath.Replace('\\', '/')}");
}
