namespace Wino.Core.Domain.Enums;

/// <summary>
/// Coverage of a single message volume histogram column on the intelligence page.
/// </summary>
public enum SemanticIndexBucketCoverage
{
    /// <summary>
    /// The bucket is outside of the range the user selected.
    /// </summary>
    Outside,

    /// <summary>
    /// The bucket is selected but its messages are not indexed yet.
    /// </summary>
    Selected,

    /// <summary>
    /// The bucket is selected and already covered by the index.
    /// </summary>
    Indexed,
}
