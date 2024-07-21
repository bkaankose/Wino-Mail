using System;
using System.Threading.Tasks;
using Wino.Domain.Models.Authorization;

namespace Wino.Domain.Interfaces
{
    public interface INativeAppService
    {
        string GetWebAuthenticationBrokerUri();
        Task<string> GetMimeMessageStoragePath();
        Task<string> GetEditorBundlePathAsync();
        Task LaunchFileAsync(string filePath);
        Task LaunchUriAsync(Uri uri);

        bool IsAppRunning();

        string GetFullAppVersion();

        Task PinAppToTaskbarAsync();

        /// <summary>
        /// Some cryptographic shit is needed for requesting Google authentication in UWP.
        /// </summary>
        GoogleAuthorizationRequest GetGoogleAuthorizationRequest();
    }
}
