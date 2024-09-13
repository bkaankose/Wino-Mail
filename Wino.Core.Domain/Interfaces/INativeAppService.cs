using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Authorization;

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
        /// Launches the default browser with the specified uri and waits for protocol activation to finish.
        /// </summary>
        /// <param name="authenticator"></param>
        /// <returns>Response callback from the browser.</returns>
        Task<Uri> GetAuthorizationResponseUriAsync(IAuthenticator authenticator, string authorizationUri, CancellationToken cancellationToken = default);

        /// <summary>
        /// Finalizes GetAuthorizationResponseUriAsync for current IAuthenticator.
        /// </summary>
        /// <param name="authorizationResponseUri"></param>
        void ContinueAuthorization(Uri authorizationResponseUri);

        bool IsAppRunning();

        string GetFullAppVersion();

        Task PinAppToTaskbarAsync();

        /// <summary>
        /// Some cryptographic shit is needed for requesting Google authentication in UWP.
        /// </summary>
        GoogleAuthorizationRequest GetGoogleAuthorizationRequest();

        /// <summary>
        /// Gets or sets the function that returns a pointer for main window hwnd for UWP.
        /// This is used to display WAM broker dialog on running UWP app called by a windowless server code.
        /// </summary>
        Func<IntPtr> GetCoreWindowHwnd { get; set; }
    }
}
