using System.Threading;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces
{
    public interface IAccountCreationDialog
    {
        void ShowDialog(CancellationTokenSource cancellationTokenSource);
        void Complete(bool cancel);
        AccountCreationDialogState State { get; set; }
    }
}
