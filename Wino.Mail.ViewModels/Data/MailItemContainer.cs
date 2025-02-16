using System;

namespace Wino.Mail.ViewModels.Data;

public class MailItemContainer
{
    public MailItemViewModel ItemViewModel { get; set; }
    public ThreadMailItemViewModel ThreadViewModel { get; set; }

    public MailItemContainer(MailItemViewModel itemViewModel, ThreadMailItemViewModel threadViewModel) : this(itemViewModel)
    {
        ThreadViewModel = threadViewModel ?? throw new ArgumentNullException(nameof(threadViewModel));
    }

    public MailItemContainer(MailItemViewModel itemViewModel)
    {
        ItemViewModel = itemViewModel ?? throw new ArgumentNullException(nameof(itemViewModel));
    }
}
