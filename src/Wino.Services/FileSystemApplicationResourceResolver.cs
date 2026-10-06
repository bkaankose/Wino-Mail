using System;
using System.IO;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

/// <summary>Resource resolution for a file-based host with explicitly supplied roots.</summary>
public sealed class FileSystemApplicationResourceResolver(
    string packagedResourceRoot,
    IApplicationConfiguration configuration) : IApplicationResourceResolver
{
    public Uri ResolvePackagedResource(string relativePath)
        => Resolve(packagedResourceRoot, relativePath);

    public Uri ResolveLocalResource(string relativePath)
        => Resolve(configuration.ApplicationDataFolderPath, relativePath);

    private static Uri Resolve(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        return new Uri(Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar))));
    }
}
