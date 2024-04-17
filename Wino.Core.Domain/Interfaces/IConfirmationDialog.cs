using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces
{
    public interface IConfirmationDialog
    {
        Task<bool> ShowDialogAsync(string title, string message, string approveButtonTitle);
    }
}
