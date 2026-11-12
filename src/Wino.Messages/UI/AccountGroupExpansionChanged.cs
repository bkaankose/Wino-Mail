using System;

namespace Wino.Messaging.UI;

/// <summary>
/// A group was expanded or collapsed in one pane. The other panes follow without rebuilding.
/// </summary>
public record AccountGroupExpansionChanged(Guid GroupId, bool IsExpanded) : UIMessageBase<AccountGroupExpansionChanged>;
