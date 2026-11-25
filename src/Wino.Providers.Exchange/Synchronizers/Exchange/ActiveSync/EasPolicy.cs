using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace Wino.Providers.Exchange.Synchronizers.Exchange.ActiveSync;

internal enum EasPolicyCompliance
{
    /// <summary>Wino applies the setting itself.</summary>
    Enforced,

    /// <summary>The setting governs a device feature Wino does not have, so it is considered enforced.</summary>
    FeatureNotSupported,

    /// <summary>The setting is empty, permissive, or only qualifies a requirement that is not set.</summary>
    NotApplicable,

    Unenforceable
}

internal sealed record EasPolicySetting(string Name, string Value, EasPolicyCompliance Compliance);

/// <summary>
/// The device policy an Exchange server sends in Provision (MS-ASPROV EASProvisionDoc), judged against what Wino can
/// enforce. An empty element means the limit is not set. Synchronization limits are applied by the client; operating
/// system controls (device password, encryption) cannot be.
/// </summary>
internal sealed class EasPolicy
{
    private static readonly HashSet<string> PasswordDetails = new(StringComparer.Ordinal)
    {
        "AlphanumericDevicePasswordRequired", "PasswordRecoveryEnabled", "MinDevicePasswordLength", "MaxInactivityTimeDeviceLock",
        "MaxDevicePasswordFailedAttempts", "AllowSimpleDevicePassword", "DevicePasswordExpiration", "DevicePasswordHistory",
        "MinDevicePasswordComplexCharacters"
    };

    private static readonly HashSet<string> SmimeDetails = new(StringComparer.Ordinal)
    {
        "RequireSignedSMIMEAlgorithm", "RequireEncryptionSMIMEAlgorithm", "AllowSMIMEEncryptionAlgorithmNegotiation", "AllowSMIMESoftCerts"
    };

    // Device hardware and operating system features that Wino neither offers nor controls.
    private static readonly HashSet<string> DeviceFeatures = new(StringComparer.Ordinal)
    {
        "AllowStorageCard", "AllowCamera", "AllowUnsignedApplications", "AllowUnsignedInstallationPackages", "AllowWiFi",
        "AllowTextMessaging", "AllowBluetooth", "AllowIrDA", "RequireManualSyncWhenRoaming", "AllowDesktopSync", "AllowBrowser",
        "AllowRemoteDesktop", "AllowInternetSharing", "UnapprovedInROMApplicationList", "ApprovedApplicationList",
        "RequireStorageCardEncryption"
    };

    private EasPolicy(IReadOnlyList<EasPolicySetting> settings, EasPolicyLimits limits)
    {
        Settings = settings;
        Limits = limits;
    }

    internal IReadOnlyList<EasPolicySetting> Settings { get; }

    /// <summary>The synchronization limits Wino enforces for this policy.</summary>
    internal EasPolicyLimits Limits { get; }

    internal IReadOnlyList<string> Unenforceable
        => Settings.Where(setting => setting.Compliance == EasPolicyCompliance.Unenforceable).Select(setting => setting.Name).ToList();

    /// <summary>
    /// MS-ASPROV 2.2.2.54.1: 1 applied, 2 applied in part with at least the PIN enabled, 3 not applied. Wino never
    /// enables a device PIN, so a policy it cannot fully enforce is acknowledged with 3.
    /// </summary>
    internal string AcknowledgementStatus => Unenforceable.Count == 0 ? "1" : "3";

    internal static EasPolicy Parse(XElement document)
    {
        var settings = new List<EasPolicySetting>();
        foreach (var element in document.Elements())
        {
            var name = element.Name.LocalName;
            var value = element.HasElements ? string.Join(",", element.Elements().Select(child => child.Value)) : element.Value.Trim();
            settings.Add(new EasPolicySetting(name, value, Judge(name, value)));
        }

        string? Value(string name)
            => settings.FirstOrDefault(setting => setting.Name == name && setting.Value.Length > 0)?.Value;

        var limits = new EasPolicyLimits(
            AgeFilter(Value("MaxEmailAgeFilter"), EmailAgeFilters),
            AgeFilter(Value("MaxCalendarAgeFilter"), CalendarAgeFilters),
            long.TryParse(Value("MaxAttachmentSize"), NumberStyles.None, CultureInfo.InvariantCulture, out var attachmentSize) ? attachmentSize : null,
            Truncation(Value("MaxEmailBodyTruncationSize")),
            Truncation(Value("MaxEmailHTMLBodyTruncationSize")),
            Value("AttachmentsEnabled") == "0");
        return new EasPolicy(settings, limits);
    }

    private static readonly int[] EmailAgeFilters = [0, 1, 2, 3, 4, 5];
    private static readonly int[] CalendarAgeFilters = [0, 4, 5, 6, 7];

    private static int AgeFilter(string? value, int[] valid)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var filter) && valid.Contains(filter) ? filter : 0;

    private static int? Truncation(string? value)
        => int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var size) && size >= 0 ? size : null;

    private static EasPolicyCompliance Judge(string name, string value)
    {
        if (PasswordDetails.Contains(name) || SmimeDetails.Contains(name))
            return EasPolicyCompliance.NotApplicable;

        if (value.Length == 0)
            return EasPolicyCompliance.NotApplicable;

        if (DeviceFeatures.Contains(name))
            return EasPolicyCompliance.FeatureNotSupported;

        return name switch
        {
            "MaxEmailAgeFilter" => IsValid(value, EmailAgeFilters) ? EasPolicyCompliance.Enforced : EasPolicyCompliance.Unenforceable,
            "MaxCalendarAgeFilter" => IsValid(value, CalendarAgeFilters) ? EasPolicyCompliance.Enforced : EasPolicyCompliance.Unenforceable,
            "MaxAttachmentSize" => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _) ? EasPolicyCompliance.Enforced : EasPolicyCompliance.Unenforceable,
            "MaxEmailBodyTruncationSize" or "MaxEmailHTMLBodyTruncationSize"
                => int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var size) && size >= -1 ? EasPolicyCompliance.Enforced : EasPolicyCompliance.Unenforceable,
            "AttachmentsEnabled" => value is "0" or "1" ? EasPolicyCompliance.Enforced : EasPolicyCompliance.Unenforceable,
            "AllowHTMLEmail" or "AllowPOPIMAPEmail" or "AllowConsumerEmail" => value == "1" ? EasPolicyCompliance.NotApplicable : EasPolicyCompliance.Unenforceable,
            "DevicePasswordEnabled" or "RequireDeviceEncryption" or "RequireSignedSMIMEMessages" or "RequireEncryptedSMIMEMessages"
                => value == "0" ? EasPolicyCompliance.NotApplicable : EasPolicyCompliance.Unenforceable,
            _ => EasPolicyCompliance.Unenforceable
        };
    }

    private static bool IsValid(string value, int[] valid)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var filter) && valid.Contains(filter);
}

/// <summary>
/// Synchronization limits from the device policy. Age filters use the AirSync FilterType values (0 means no limit);
/// a null size or truncation means no limit.
/// </summary>
internal sealed record EasPolicyLimits(int MaxEmailAgeFilter, int MaxCalendarAgeFilter, long? MaxAttachmentSize,
    int? MaxEmailBodyTruncationSize, int? MaxEmailHtmlBodyTruncationSize, bool AttachmentsDisabled)
{
    internal static readonly EasPolicyLimits None = new(0, 0, null, null, null, false);

    /// <summary>
    /// The FilterType to send for a collection: the client's own choice narrowed to the policy's maximum. Email and
    /// calendar filter values grow with the window they keep, so the smaller non-zero value is the narrower one.
    /// </summary>
    internal int ClampFilterType(string itemClass, int requested)
    {
        var maximum = itemClass switch
        {
            "Email" => MaxEmailAgeFilter,
            "Calendar" => MaxCalendarAgeFilter,
            _ => 0
        };
        if (maximum == 0)
            return requested;
        return requested == 0 ? maximum : Math.Min(requested, maximum);
    }

    /// <summary>The strictest body truncation the policy sets, or null when it sets none.</summary>
    internal int? BodyTruncation
        => (MaxEmailBodyTruncationSize, MaxEmailHtmlBodyTruncationSize) switch
        {
            ({ } plain, { } html) => Math.Min(plain, html),
            ({ } plain, null) => plain,
            (null, { } html) => html,
            _ => null
        };
}
