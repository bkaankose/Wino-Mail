namespace Wino.Mail.Controls.Core.HoverActions;

public static class HoverActionConfiguration
{
    public static IReadOnlyList<HoverActionKind> GetVisibleActions(
        HoverActionKind left,
        HoverActionKind center,
        HoverActionKind right)
    {
        HoverActionKind[] configuredActions = [left, center, right];

        return configuredActions
            .Where(static action => action != HoverActionKind.None)
            .ToArray();
    }
}
