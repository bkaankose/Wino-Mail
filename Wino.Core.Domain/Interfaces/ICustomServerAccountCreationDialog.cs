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

        /// <summary>
        /// Updates account properties for the welcome imap setup dialog and starts the setup.
        /// </summary>
        /// <param name="account">Account properties.</param>
        void StartImapConnectionSetup(MailAccount account);
    }
}
