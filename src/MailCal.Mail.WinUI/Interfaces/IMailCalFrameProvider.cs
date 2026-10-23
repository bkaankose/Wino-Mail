using Microsoft.UI.Xaml.Controls;
using MailCal.Core.Domain.Enums;

namespace MailCal.Mail.WinUI.Interfaces;

public interface IMailCalFrameProvider
{
    Frame? GetFrame(NavigationReferenceFrame frameType);
}
