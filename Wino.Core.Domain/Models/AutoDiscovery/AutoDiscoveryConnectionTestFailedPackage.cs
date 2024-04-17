using System;

namespace Wino.Core.Domain.Models.AutoDiscovery
{
    public class AutoDiscoveryConnectionTestFailedPackage
    {
        public AutoDiscoveryConnectionTestFailedPackage(AutoDiscoverySettings settings, Exception error)
        {
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            Error = error ?? throw new ArgumentNullException(nameof(error));
        }

        public AutoDiscoveryConnectionTestFailedPackage(Exception error)
        {
            Error = error ?? throw new ArgumentNullException(nameof(error));
        }

        public AutoDiscoverySettings Settings { get; set; }
        public Exception Error { get; set; }
    }
}
