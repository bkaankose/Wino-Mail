using System.Threading.Tasks;

namespace Wino.Domain.Interfaces
{
    public interface IConfirmationDialog
    {
        Task<bool> ShowDialogAsync(string title, string message, string approveButtonTitle);
    }
}
