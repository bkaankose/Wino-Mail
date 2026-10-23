using System.Threading;
using System.Threading.Tasks;
using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.Interfaces;

public interface IAccountCreationDialog
{
    Task ShowDialogAsync(CancellationTokenSource cancellationTokenSource);
    void Complete(bool cancel);
    AccountCreationDialogState State { get; set; }
}
