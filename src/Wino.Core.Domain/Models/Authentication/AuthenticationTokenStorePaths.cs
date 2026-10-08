using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Models.Authentication;

public static class AuthenticationTokenStorePaths
{
    public const string OutlookTokenCacheFileName = "OutlookCache.bin";
    public const string GmailTokenStoreFolderName = "WinoMailGmailTokenStore";
    public const string LegacyGoogleTokenFilePrefix = "Google.Apis.Auth.OAuth2.Responses.TokenResponse-";

    public static string GetOutlookTokenCachePath(IApplicationConfiguration configuration)
        => Path.Combine(GetLocalStatePath(configuration), OutlookTokenCacheFileName);

    public static string GetLegacyOutlookTokenCachePath(IApplicationConfiguration configuration)
        => Path.Combine(configuration.PublisherSharedFolderPath, OutlookTokenCacheFileName);

    public static string GetGmailTokenStorePath(IApplicationConfiguration configuration)
        => Path.Combine(GetLocalStatePath(configuration), GmailTokenStoreFolderName);

    public static string GetGmailTokenPath(IApplicationConfiguration configuration, Guid accountId)
        => Path.Combine(GetGmailTokenStorePath(configuration), $"{accountId:N}.json");

    public static string GetLegacyPublisherGmailTokenStorePath(IApplicationConfiguration configuration)
        => Path.Combine(configuration.PublisherSharedFolderPath, GmailTokenStoreFolderName);

    public static IReadOnlyList<string> GetLegacyGmailTokenStorePaths(IApplicationConfiguration configuration)
    {
        var paths = new List<string>();

        AddDistinctPath(paths, GetLegacyPublisherGmailTokenStorePath(configuration));

        var localStatePath = GetLocalStatePath(configuration);
        var packageDataPath = Directory.GetParent(localStatePath)?.FullName;
        if (string.Equals(Path.GetFileName(localStatePath), "LocalState", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(packageDataPath))
        {
            AddDistinctPath(paths, Path.Combine(
                packageDataPath,
                "LocalCache",
                "Roaming",
                GmailTokenStoreFolderName));
        }

        AddDistinctPath(paths, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            GmailTokenStoreFolderName));

        return paths;
    }

    public static string GetLegacyGoogleTokenPath(string tokenStorePath, Guid accountId)
        => Path.Combine(tokenStorePath, $"{LegacyGoogleTokenFilePrefix}{accountId:D}");

    private static string GetLocalStatePath(IApplicationConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (string.IsNullOrWhiteSpace(configuration.ApplicationDataFolderPath))
            throw new InvalidOperationException("The application LocalState path is not initialized.");

        return configuration.ApplicationDataFolderPath;
    }

    private static void AddDistinctPath(ICollection<string> paths, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var fullPath = Path.GetFullPath(path);
        if (!paths.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
            paths.Add(fullPath);
    }
}
