using Windows.Storage;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.UWP.Services
{
    public class AppInitializerService : IAppInitializerService
    {
        public const string SharedFolderName = "WinoShared";

        public string GetPublisherSharedFolder() => ApplicationData.Current.GetPublisherCacheFolder(SharedFolderName).Path;
        public string GetApplicationDataFolder() => ApplicationData.Current.LocalFolder.Path;
    }
}
