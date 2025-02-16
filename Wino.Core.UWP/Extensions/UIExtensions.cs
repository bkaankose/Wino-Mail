using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;

namespace Wino.Extensions
{
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
}
