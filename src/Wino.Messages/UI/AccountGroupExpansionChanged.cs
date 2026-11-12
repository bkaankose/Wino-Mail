using System;

namespace Wino.Messaging.UI;

/// <summary>
/// A group was expanded or collapsed in one pane. The other panes follow without rebuilding; the pane that made
/// the change is <paramref name="Origin"/> and skips it, so a late echo cannot undo a newer toggle.
/// </summary>
public record AccountGroupExpansionChanged(Guid GroupId, bool IsExpanded, object Origin) : UIMessageBase<AccountGroupExpansionChanged>;
