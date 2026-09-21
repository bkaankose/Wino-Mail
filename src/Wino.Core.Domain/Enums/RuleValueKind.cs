namespace Wino.Core.Domain.Enums;

/// <summary>
/// The kind of value a rule condition/action takes. Drives the adaptive value editor in the rule UI
/// and how the value string is interpreted when mapping to/from the server.
/// </summary>
public enum RuleValueKind
{
    /// <summary>No value (e.g. Has attachment, Mark as read, Delete).</summary>
    None,
    /// <summary>A folder (value carries the folder's remote id).</summary>
    Folder,
    /// <summary>A category name.</summary>
    Category,
    /// <summary>High / Normal / Low.</summary>
    Importance,
    /// <summary>One or more SMTP addresses (comma-separated).</summary>
    Sender,
    /// <summary>Free text to match.</summary>
    Text
}
