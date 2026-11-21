using Wino.Mail.Linux.Infrastructure;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>Print and PDF export of the message as the reader shows it, through a presenter bound to this reader's web view.</summary>
internal sealed partial class MailReaderView
{
    partial void InitializePrint()
    {
        var self = new WeakReference<MailReaderView>(this);
        ViewModel.PrintPresenter = new LinuxMailPrintPresenter(() => self.TryGetTarget(out var view) && !view.IsReleased ? view._reader?.View : null, Dispatcher);
        Bindings.Own(new ActionDisposable(() => ViewModel.PrintPresenter = null));
    }
}
