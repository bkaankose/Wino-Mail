using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels.Messages
{
    /// <summary>
    /// When the rendering page is active, but new item is requested to be rendered.
    /// To not trigger navigation again and re-use existing Chromium.
    /// </summary>
    /// <param name="MailItemViewModel"></param>
    public record NewMailItemRenderingRequestedEvent(MailItemViewModel MailItemViewModel);
}
