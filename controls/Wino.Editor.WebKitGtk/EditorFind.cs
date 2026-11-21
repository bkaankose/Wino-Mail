namespace Wino.Editor.WebKitGtk;

public enum EditorFindDirection
{
    Start,
    Next,
    Previous
}

/// <summary>One-based current match and total; (0, 0) when nothing matches.</summary>
public readonly record struct EditorFindResult(int Index, int Count);
