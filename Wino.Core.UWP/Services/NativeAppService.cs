using System;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Foundation.Metadata;
using Windows.Security.Authentication.Web;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Shell;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authorization;

namespace Wino.Services
{
    public class NativeAppService : INativeAppService
    {
        private string _mimeMessagesFolder;
        private string _editorBundlePath;

        public string GetWebAuthenticationBrokerUri() => WebAuthenticationBroker.GetCurrentApplicationCallbackUri().AbsoluteUri;

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
                var editorFileFromBundle = await StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///JS/Quill/editor.html"))
                    .AsTask()
                    .ConfigureAwait(false);

                _editorBundlePath = editorFileFromBundle.Path;
            }

            return _editorBundlePath;
        }

        public bool IsAppRunning() => (Window.Current?.Content as Frame)?.Content != null;

        public async Task LaunchFileAsync(string filePath)
        {
            var file = await StorageFile.GetFileFromPathAsync(filePath);

            await Launcher.LaunchFileAsync(file);
        }

        public Task LaunchUriAsync(Uri uri) => Xamarin.Essentials.Launcher.OpenAsync(uri);

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
    }
}
