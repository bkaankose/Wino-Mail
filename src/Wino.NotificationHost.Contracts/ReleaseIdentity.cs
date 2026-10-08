using System.Collections.ObjectModel;
using System.Text.Json;

namespace Wino.NotificationHost.Contracts;

/// <summary>Package-owned metadata shared by the UI and all notification processes.</summary>
public sealed class ReleaseIdentity
{
    private static ReleaseIdentity? _current;
    public static ReleaseIdentity Current => _current ?? throw new InvalidOperationException("Release identity has not been initialized.");

    public string Distribution { get; }
    public string PackageFamilyName { get; }
    public string MailDisplayName => DisplayNames["Mail"];
    public IReadOnlyDictionary<string, string> DisplayNames { get; }
    public IReadOnlyDictionary<string, Guid> NotificationActivatorIds { get; }
    public bool AllowsLegacyMigration => Distribution is "Store" or "Sideload";
    public string SingleInstanceKey => $"WinoMail.{PackageFamilyName}.SingleInstance";
    public string MailHostMutexName => $"Local\\WinoMail.{PackageFamilyName}.MailHostRunning";
    public string AlternateModeEventName => $"Local\\WinoMail.{PackageFamilyName}.ForceAlternateMode";

    private ReleaseIdentity(string distribution, string familyName, Dictionary<string, string> names, Dictionary<string, Guid> ids)
    {
        Distribution = distribution;
        PackageFamilyName = familyName;
        DisplayNames = new ReadOnlyDictionary<string, string>(names);
        NotificationActivatorIds = new ReadOnlyDictionary<string, Guid>(ids);
    }

    public static void Initialize(string directory, string packageName, string publisher, string familyName)
        => _current = Load(Path.Combine(directory, "release-profile.json"), packageName, publisher, familyName);

    public static ReleaseIdentity Load(string path, string packageName, string publisher, string familyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(familyName);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var distribution = root.GetProperty("Distribution").GetString();
        var expectedName = distribution switch
        {
            "Store" => "58272BurakKSE.WinoMailPreview",
            "Sideload" => "WinoMail.Sideload",
            "Beta" => "WinoMail.Beta",
            _ => throw new InvalidDataException("Unknown release distribution.")
        };

        if (packageName != expectedName || root.GetProperty("PackageName").GetString() != packageName ||
            root.GetProperty("Publisher").GetString() != publisher)
            throw new InvalidDataException("The release profile does not match the installed package identity.");

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var entry in new[] { "Mail", "Calendar", "People", "Tasks" })
        {
            var name = root.GetProperty("DisplayNames").GetProperty(entry).GetString();
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidDataException("An application display name is missing.");

            names.Add(entry, name);
            var id = root.GetProperty("NotificationActivatorIds").GetProperty(entry).GetGuid();
            if (id == Guid.Empty || ids.ContainsValue(id))
                throw new InvalidDataException("Notification activator IDs must be nonempty and distinct.");

            ids.Add(entry, id);
        }

        return new ReleaseIdentity(distribution!, familyName, names, ids);
    }
}
