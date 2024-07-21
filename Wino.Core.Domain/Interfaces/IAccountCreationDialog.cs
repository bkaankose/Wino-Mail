using Wino.Domain.Enums;

namespace Wino.Domain.Interfaces
{
    public interface IAccountCreationDialog
    {
        void ShowDialog();
        void Complete();
        AccountCreationDialogState State { get; set; }
    }
}
