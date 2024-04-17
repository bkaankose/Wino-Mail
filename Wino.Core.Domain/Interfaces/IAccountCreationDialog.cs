using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces
{
    public interface IAccountCreationDialog
    {
        void ShowDialog();
        void Complete();
        AccountCreationDialogState State { get; set; }
    }
}
