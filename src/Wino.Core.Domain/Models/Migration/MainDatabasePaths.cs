using System;
using System.IO;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Models.Migration;

public static class MainDatabasePaths
{
    public static string GetRoot(IApplicationConfiguration configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.ApplicationDataFolderPath);
        return Path.GetFullPath(configuration.ApplicationDataFolderPath);
    }

    public static string GetPath(IApplicationConfiguration configuration, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName)
            throw new ArgumentException("A database file name without directories is required.", nameof(fileName));

        return Path.Combine(GetRoot(configuration), fileName);
    }
}
