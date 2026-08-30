using System.Collections.ObjectModel;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// One section of the contacts sidebar. The pane draws a rule between sections, so a
/// group carries no caption of its own.
/// </summary>
public partial class ContactFilterGroup : ObservableCollection<ContactFilterViewModel>;
