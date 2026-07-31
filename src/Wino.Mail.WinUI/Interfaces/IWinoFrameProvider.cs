using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.WinUI.Interfaces;

public interface IWinoFrameProvider
{
    Frame? GetFrame(NavigationReferenceFrame frameType);
}
