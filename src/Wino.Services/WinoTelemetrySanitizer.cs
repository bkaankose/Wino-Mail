using System;
using System.Collections.Generic;
using System.Linq;

namespace Wino.Services;

public static class WinoTelemetrySanitizer
{
    public const int MaxPropertyValueLength = 200;

    private static readonly string[] ForbiddenKeyFragments =
    [
        "password",
        "token",
        "secret",
        "username",
        "email",
        "address",
        "local_part",
        "subject",
        "content",
        "body",
        "query",
        "path",
        "account_id",
        "folder_id",
        "folder_name",
        "calendar_id",
        "calendar_name",
        "message_id",
        "mail_id"
    ];

    private static readonly HashSet<string> SearchableTagKeys = new(StringComparer.Ordinal)
    {
        "app_close_behavior",
        "calendar_enabled",
        "certificate_action",
        "certificate_error_kind",
        "error_origin",
        "event_kind",
        "exception_type",
        "failure_category",
        "failure_stage",
        "feature",
        "has_configured_accounts",
        "icon_exists",
        "is_calendar_access_enabled",
        "is_mail_access_enabled",
        "is_oauth_provider",
        "mail_enabled",
        "operation",
        "provider",
        "provider_type",
        "result",
        "setup_operation",
        "special_provider",
        "state",
        "sync_area",
        "sync_completed_state",
        "sync_issue_category",
        "sync_issue_operation",
        "sync_issue_severity",
        "sync_type"
    };

    public static Dictionary<string, string> CreateSafeProperties(
        IReadOnlyDictionary<string, string>? properties)
    {
        var safeProperties = new Dictionary<string, string>(StringComparer.Ordinal);

        if (properties == null)
        {
            return safeProperties;
        }

        foreach (var property in properties.Where(property => !string.IsNullOrWhiteSpace(property.Key)))
        {
            var key = property.Key.Trim();
            if (property.Value == null || IsForbiddenPropertyKey(key) || LooksLikeSensitiveValue(property.Value))
            {
                continue;
            }

            safeProperties[key] = NormalizeValue(property.Value);
        }

        return safeProperties;
    }

    public static bool IsForbiddenPropertyKey(string key)
    {
        var normalizedKey = key.Trim().ToLowerInvariant();

        if (string.Equals(normalizedKey, "diagnostic_id", StringComparison.Ordinal))
        {
            return false;
        }

        return ForbiddenKeyFragments.Any(fragment => normalizedKey.Contains(fragment, StringComparison.Ordinal));
    }

    public static bool IsSearchableTag(string key)
        => SearchableTagKeys.Contains(key);

    public static string NormalizeValue(string value)
    {
        var normalizedValue = value.Trim();
        return normalizedValue.Length <= MaxPropertyValueLength
            ? normalizedValue
            : normalizedValue[..MaxPropertyValueLength];
    }

    private static bool LooksLikeSensitiveValue(string value)
    {
        var normalizedValue = value.Trim();

        return normalizedValue.Contains('@', StringComparison.Ordinal)
               || normalizedValue.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
               || normalizedValue.Contains("://", StringComparison.Ordinal);
    }
}
