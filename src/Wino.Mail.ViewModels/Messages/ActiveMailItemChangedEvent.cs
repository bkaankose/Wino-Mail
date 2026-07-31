using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels.Messages;

/// <summary>
/// When active mail item in the reader is updated.
/// </summary>
public class ActiveMailItemChangedEvent
{
    public ActiveMailItemChangedEvent(MailItemViewModel selectedMailItemViewModel)
    {
        // SelectedMailItemViewModel can be null.
        SelectedMailItemViewModel = selectedMailItemViewModel;
    }

    public MailItemViewModel SelectedMailItemViewModel { get; set; }
}
