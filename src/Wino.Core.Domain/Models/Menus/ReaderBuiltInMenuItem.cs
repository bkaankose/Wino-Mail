using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Menus;

/// <summary>
/// Places a command the platform command bar owns (pop out, editor theme, reader view) inside the reader layout.
/// </summary>
public class ReaderBuiltInMenuItem : MenuOperationItemBase<ReaderCommandAction>
{
    private ReaderBuiltInMenuItem(ReaderCommandAction action, bool isSecondaryMenuItem)
        : base(action, true, isSecondaryMenuItem)
    { }

    public static ReaderBuiltInMenuItem Create(ReaderCommandAction action, bool isSecondaryMenuItem = false)
        => new ReaderBuiltInMenuItem(action, isSecondaryMenuItem);
}
