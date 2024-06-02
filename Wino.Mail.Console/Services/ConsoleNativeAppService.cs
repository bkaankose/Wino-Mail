using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authorization;

namespace Wino.Mail.ConsoleTest.Services
{
    internal class ConsoleNativeAppService : INativeAppService
    {
        public string GetFullAppVersion() => "1.0.0";

        public GoogleAuthorizationRequest GetGoogleAuthorizationRequest() => null;

        public async Task<string> GetMimeMessageStoragePath() => "C:\\Users\\bkaan\\Desktop\\WinoTest\\Mime";

        public Task<string> GetQuillEditorBundlePathAsync()
        {
            throw new NotImplementedException();
        }

        public string GetWebAuthenticationBrokerUri() => "http://localhost";

        public bool IsAppRunning()
        {
            throw new NotImplementedException();
        }

        public Task LaunchFileAsync(string filePath)
        {
            throw new NotImplementedException();
        }

        public Task LaunchUriAsync(Uri uri)
        {
            throw new NotImplementedException();
        }

        public Task PinAppToTaskbarAsync()
        {
            throw new NotImplementedException();
        }
    }
}
