using Microsoft.UI.Xaml.Controls;
using MailCal.Core.Domain.Enums;

namespace MailCal.Extensions;

public static class UIExtensions
{
    public static InfoBarSeverity AsMUXCInfoBarSeverity(this InfoBarMessageType messageType)
    {
        return messageType switch
        {
            InfoBarMessageType.Error => InfoBarSeverity.Error,
            InfoBarMessageType.Warning => InfoBarSeverity.Warning,
            InfoBarMessageType.Information => InfoBarSeverity.Informational,
            InfoBarMessageType.Success => InfoBarSeverity.Success,
            _ => InfoBarSeverity.Informational
        };
    }
}
