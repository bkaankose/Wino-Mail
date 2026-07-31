using System;

namespace Wino.Mail.ViewModels.Data;

public class MailItemContainer
{
    public MailItemViewModel ItemViewModel { get; set; }
    public ThreadMailItemViewModel ThreadViewModel { get; set; }
    
    /// <summary>
    /// Indicates whether the mail item is currently visible in the UI's Items collection.
    /// For threaded items, this indicates if the individual mail item is visible (thread must be expanded).
    /// </summary>
    public bool IsItemVisible { get; set; }
    
    /// <summary>
    /// Indicates whether the thread expander (if applicable) is currently visible in the UI's Items collection.
    /// Only relevant when ThreadViewModel is not null.
    /// </summary>
    public bool IsThreadVisible { get; set; }
    
    /// <summary>
    /// Indicates whether the container can be successfully navigated to in the UI.
    /// For standalone items: true if IsItemVisible is true.
    /// For threaded items: true if IsThreadVisible is true (the thread expander can be navigated to).
    /// </summary>
    public bool CanNavigate => ThreadViewModel != null ? IsThreadVisible : IsItemVisible;

    public MailItemContainer(MailItemViewModel itemViewModel, ThreadMailItemViewModel threadViewModel) : this(itemViewModel)
    {
        ThreadViewModel = threadViewModel ?? throw new ArgumentNullException(nameof(threadViewModel));
    }

    public MailItemContainer(MailItemViewModel itemViewModel)
    {
        ItemViewModel = itemViewModel ?? throw new ArgumentNullException(nameof(itemViewModel));
    }
}
