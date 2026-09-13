using System;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.WinUI.ThirdParty.DesktopFlyouts;

internal static class CompanionFlyoutPlacementCalculator
{
    internal static CompanionRect Calculate(
        CompanionRect workArea,
        CompanionRect? iconRect,
        WindowsTaskbarPosition taskbarPosition,
        int flyoutWidth,
        int flyoutHeight,
        int gap)
    {
        var x = workArea.X;
        var y = workArea.Y;

        if (iconRect is { } icon)
        {
            switch (taskbarPosition)
            {
                case WindowsTaskbarPosition.Left:
                    x = icon.X + icon.Width + gap;
                    y = icon.Y + ((icon.Height - flyoutHeight) / 2);
                    break;
                case WindowsTaskbarPosition.Top:
                    x = icon.X + ((icon.Width - flyoutWidth) / 2);
                    y = icon.Y + icon.Height + gap;
                    break;
                case WindowsTaskbarPosition.Right:
                    x = icon.X - flyoutWidth - gap;
                    y = icon.Y + ((icon.Height - flyoutHeight) / 2);
                    break;
                default:
                    x = icon.X + ((icon.Width - flyoutWidth) / 2);
                    y = icon.Y - flyoutHeight - gap;
                    break;
            }
        }
        else
        {
            switch (taskbarPosition)
            {
                case WindowsTaskbarPosition.Left:
                    x = workArea.X + gap;
                    y = workArea.Y + ((workArea.Height - flyoutHeight) / 2);
                    break;
                case WindowsTaskbarPosition.Top:
                    x = workArea.X + workArea.Width - flyoutWidth - gap;
                    y = workArea.Y + gap;
                    break;
                case WindowsTaskbarPosition.Right:
                    x = workArea.X + workArea.Width - flyoutWidth - gap;
                    y = workArea.Y + ((workArea.Height - flyoutHeight) / 2);
                    break;
                default:
                    x = workArea.X + workArea.Width - flyoutWidth - gap;
                    y = workArea.Y + workArea.Height - flyoutHeight - gap;
                    break;
            }
        }

        var maxX = workArea.X + Math.Max(0, workArea.Width - flyoutWidth);
        var maxY = workArea.Y + Math.Max(0, workArea.Height - flyoutHeight);
        x = Math.Clamp(x, workArea.X, maxX);
        y = Math.Clamp(y, workArea.Y, maxY);

        return new CompanionRect(x, y, flyoutWidth, flyoutHeight);
    }
}

internal readonly record struct CompanionRect(int X, int Y, int Width, int Height);
