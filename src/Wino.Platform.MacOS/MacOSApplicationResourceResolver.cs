using System;
using System.IO;
using System.Runtime.Versioning;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.MacOS;

[SupportedOSPlatform("macos")]
public sealed class MacOSApplicationResourceResolver(MacOSPaths paths) : IApplicationResourceResolver
{
    public Uri ResolvePackagedResource(string relativePath) => Resolve(paths.PackagedResourceRoot, relativePath);
    public Uri ResolveLocalResource(string relativePath) => Resolve(paths.ApplicationDataRoot, relativePath);

    private static Uri Resolve(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath)) throw new ArgumentException("An application resource path must be relative.");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(fullRoot, StringComparison.Ordinal))
            throw new ArgumentException("An application resource path must remain inside its root.");
        return new Uri(fullPath);
    }
}
