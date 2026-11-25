using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace Wino.Providers.Exchange.Synchronizers.Exchange.ActiveSync;

internal enum EasPolicyCompliance
{
    Honoured,
    NotApplicable,
    Unenforceable
}

internal sealed record EasPolicySetting(string Name, string Value, EasPolicyCompliance Compliance);

/// <summary>
/// The device policy an Exchange server sends in Provision (MS-ASPROV EASProvisionDoc), judged against what Wino can
/// honour. An empty element means the limit is not set. Wino cannot enforce operating system controls (device
/// password, encryption, hardware), so those are reported as unenforceable and the server decides.
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

    // Wino offers neither desktop synchronization nor SMS synchronization, so forbidding them is already true.
    private static readonly HashSet<string> AbsentFeatures = new(StringComparer.Ordinal) { "AllowDesktopSync", "AllowTextMessaging" };

    private EasPolicy(IReadOnlyList<EasPolicySetting> settings) => Settings = settings;

    internal IReadOnlyList<EasPolicySetting> Settings { get; }

    internal IReadOnlyList<string> Unenforceable
        => Settings.Where(setting => setting.Compliance == EasPolicyCompliance.Unenforceable).Select(setting => setting.Name).ToList();

    /// <summary>MS-ASPROV acknowledgement: 1 applied, 2 applied in part.</summary>
    internal string AcknowledgementStatus => Unenforceable.Count == 0 ? "1" : "2";

    internal static EasPolicy Parse(XElement document)
    {
        var settings = new List<EasPolicySetting>();
        foreach (var element in document.Elements())
        {
            var name = element.Name.LocalName;
            var value = element.HasElements ? string.Join(",", element.Elements().Select(child => child.Value)) : element.Value.Trim();
            settings.Add(new EasPolicySetting(name, value, Judge(name, value, element.HasElements)));
        }
        return new EasPolicy(settings);
    }

    private static EasPolicyCompliance Judge(string name, string value, bool hasChildren)
    {
        if (PasswordDetails.Contains(name) || SmimeDetails.Contains(name))
            return EasPolicyCompliance.NotApplicable;

        if (name is "UnapprovedInROMApplicationList" or "ApprovedApplicationList")
            return hasChildren ? EasPolicyCompliance.Unenforceable : EasPolicyCompliance.NotApplicable;

        if (value.Length == 0)
            return EasPolicyCompliance.NotApplicable;

        return IsPermissive(name, value) || AbsentFeatures.Contains(name)
            ? EasPolicyCompliance.Honoured
            : EasPolicyCompliance.Unenforceable;
    }

    private static bool IsPermissive(string name, string value) => name switch
    {
        "AllowBluetooth" => value == "2",
        "MaxEmailBodyTruncationSize" or "MaxEmailHTMLBodyTruncationSize" => value == "-1",
        "MaxCalendarAgeFilter" or "MaxEmailAgeFilter" or "MaxAttachmentSize" => value == "0",
        "AttachmentsEnabled" => value == "1",
        _ when name.StartsWith("Allow", StringComparison.Ordinal) => value == "1",
        _ when name.StartsWith("Require", StringComparison.Ordinal) || name == "DevicePasswordEnabled" => value == "0",
        _ => false
    };
}
