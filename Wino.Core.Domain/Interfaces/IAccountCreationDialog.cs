using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces
{
    public interface IAccountCreationDialog
    {
        Task ShowDialogAsync(CancellationTokenSource cancellationTokenSource);
        void Complete(bool cancel);
        AccountCreationDialogState State { get; set; }
    }
}
