using Wino.Domain.Interfaces;

namespace Wino.Services.Services
{
    public class ApplicationConfiguration : IApplicationConfiguration
    {
        public const string SharedFolderName = "WinoShared";

        public string ApplicationDataFolderPath { get; set; }

        public string PublisherSharedFolderPath { get; set; }
    }
}
