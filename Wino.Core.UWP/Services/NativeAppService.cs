using System;
using System.Threading;
using System.Threading.Tasks;
using Nito.AsyncEx;
using Windows.ApplicationModel;
using Windows.Foundation.Metadata;
using Windows.Security.Authentication.Web;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Shell;
using Wino.Core.Domain;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authorization;



#if WINDOWS_UWP
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
#endif

namespace Wino.Services
{
    public class NativeAppService : INativeAppService
    {
        private string _mimeMessagesFolder;
        private string _editorBundlePath;
        private TaskCompletionSource<Uri> authorizationCompletedTaskSource;

        public Func<IntPtr> GetCoreWindowHwnd { get; set; }

        public string GetWebAuthenticationBrokerUri()
        {
#if WINDOWS_UWP
            return WebAuthenticationBroker.GetCurrentApplicationCallbackUri().AbsoluteUri;
#endif

            return string.Empty;
        }

        public async Task<string> GetMimeMessageStoragePath()
        {
            if (!string.IsNullOrEmpty(_mimeMessagesFolder))
                return _mimeMessagesFolder;

            var localFolder = ApplicationData.Current.LocalFolder;
            var mimeFolder = await localFolder.CreateFolderAsync("Mime", CreationCollisionOption.OpenIfExists);

            _mimeMessagesFolder = mimeFolder.Path;

            return _mimeMessagesFolder;
        }

        #region Cryptography

        public string randomDataBase64url(uint length)
        {
            IBuffer buffer = CryptographicBuffer.GenerateRandom(length);
            return base64urlencodeNoPadding(buffer);
        }

        public IBuffer sha256(string inputString)
        {
            HashAlgorithmProvider sha = HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Sha256);
            IBuffer buff = CryptographicBuffer.ConvertStringToBinary(inputString, BinaryStringEncoding.Utf8);
            return sha.HashData(buff);
        }

        public string base64urlencodeNoPadding(IBuffer buffer)
        {
            string base64 = CryptographicBuffer.EncodeToBase64String(buffer);

            // Converts base64 to base64url.
            base64 = base64.Replace("+", "-");
            base64 = base64.Replace("/", "_");

            // Strips padding.
            base64 = base64.Replace("=", "");

            return base64;
        }

        #endregion

        // GMail Integration.
        public GoogleAuthorizationRequest GetGoogleAuthorizationRequest()
        {
            string state = randomDataBase64url(32);
            string code_verifier = randomDataBase64url(32);
            string code_challenge = base64urlencodeNoPadding(sha256(code_verifier));

            return new GoogleAuthorizationRequest(state, code_verifier, code_challenge);
        }

        public async Task<string> GetEditorBundlePathAsync()
        {
            if (string.IsNullOrEmpty(_editorBundlePath))
            {
                var editorFileFromBundle = await StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///JS/editor.html"))
                    .AsTask()
                    .ConfigureAwait(false);

                _editorBundlePath = editorFileFromBundle.Path;
            }

            return _editorBundlePath;
        }

        [Obsolete("This should be removed. There should be no functionality.")]
        public bool IsAppRunning()
        {
#if WINDOWS_UWP
            return (Window.Current?.Content as Frame)?.Content != null;
#endif

            return true;
        }


        public async Task LaunchFileAsync(string filePath)
        {
            var file = await StorageFile.GetFileFromPathAsync(filePath);

            await Launcher.LaunchFileAsync(file);
        }

        public Task<bool> LaunchUriAsync(Uri uri) => Launcher.LaunchUriAsync(uri).AsTask();

        public string GetFullAppVersion()
        {
            Package package = Package.Current;
            PackageId packageId = package.Id;
            PackageVersion version = packageId.Version;

            return string.Format("{0}.{1}.{2}.{3}", version.Major, version.Minor, version.Build, version.Revision);
        }

        public async Task PinAppToTaskbarAsync()
        {
            // If Start screen manager API's aren't present
            if (!ApiInformation.IsTypePresent("Windows.UI.Shell.TaskbarManager")) return;

            // Get the taskbar manager
            var taskbarManager = TaskbarManager.GetDefault();

            // If Taskbar doesn't allow pinning, don't show the tip
            if (!taskbarManager.IsPinningAllowed) return;

            // If already pinned, don't show the tip
            if (await taskbarManager.IsCurrentAppPinnedAsync()) return;

            await taskbarManager.RequestPinCurrentAppAsync();
        }

        public async Task<Uri> GetAuthorizationResponseUriAsync(IAuthenticator authenticator, string authorizationUri, CancellationToken cancellationToken = default)
        {
            if (authorizationCompletedTaskSource != null)
            {
                authorizationCompletedTaskSource.TrySetException(new AuthenticationException(Translator.Exception_AuthenticationCanceled));
                authorizationCompletedTaskSource = null;
            }

            authorizationCompletedTaskSource = new TaskCompletionSource<Uri>();

            bool isLaunched = await Launcher.LaunchUriAsync(new Uri(authorizationUri)).AsTask(cancellationToken);

            if (!isLaunched)
                throw new WinoServerException("Failed to launch Google Authentication dialog.");

            return await authorizationCompletedTaskSource.Task.WaitAsync(cancellationToken);
        }

        public void ContinueAuthorization(Uri authorizationResponseUri)
        {
            if (authorizationCompletedTaskSource != null)
            {
                authorizationCompletedTaskSource.TrySetResult(authorizationResponseUri);
            }
        }
    }
}
