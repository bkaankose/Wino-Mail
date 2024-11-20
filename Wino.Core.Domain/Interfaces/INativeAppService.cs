using System;
using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces
{
    public interface INativeAppService
    {
        string GetWebAuthenticationBrokerUri();
        Task<string> GetMimeMessageStoragePath();
        Task<string> GetEditorBundlePathAsync();
        Task LaunchFileAsync(string filePath);
        Task<bool> LaunchUriAsync(Uri uri);

        /// <summary>
        /// Finalizes GetAuthorizationResponseUriAsync for current IAuthenticator.
        /// </summary>
        /// <param name="authorizationResponseUri"></param>
        void ContinueAuthorization(Uri authorizationResponseUri);

        bool IsAppRunning();

        string GetFullAppVersion();

        Task PinAppToTaskbarAsync();

        /// <summary>
        /// Gets or sets the function that returns a pointer for main window hwnd for UWP.
        /// This is used to display WAM broker dialog on running UWP app called by a windowless server code.
        /// </summary>
        Func<IntPtr> GetCoreWindowHwnd { get; set; }
    }
}
