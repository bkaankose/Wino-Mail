using Windows.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.Uwp.Interfaces;

public interface IWinoFrameProvider
{
    Frame? GetFrame(NavigationReferenceFrame frameType);
}
