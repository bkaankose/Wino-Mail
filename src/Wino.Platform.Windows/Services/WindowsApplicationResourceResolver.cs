using System;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.WinUI.Services;

public sealed class WindowsApplicationResourceResolver : IApplicationResourceResolver
{
    public Uri ResolvePackagedResource(string relativePath) => GetPackagedResourceUri(relativePath);

    public Uri ResolveLocalResource(string relativePath)
        => new($"ms-appdata:///local/{relativePath.Replace('\\', '/')}");

    public static Uri GetPackagedResourceUri(string relativePath)
        => new($"ms-appx:///{relativePath.Replace('\\', '/')}");
}
