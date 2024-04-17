using System.Threading.Tasks;
using Wino.Core.Domain.Entities;

namespace Wino.Core.Domain.Interfaces
{
    public interface ICustomServerAccountCreationDialog : IAccountCreationDialog
    {
        /// <summary>
        /// Returns the custom server information from the dialog..
        /// </summary>
        /// <returns>Null if canceled.</returns>
        Task<CustomServerInformation> GetCustomServerInformationAsync();

        /// <summary>
        /// Displays preparing folders page.
        /// </summary>
        void ShowPreparingFolders();
    }
}
