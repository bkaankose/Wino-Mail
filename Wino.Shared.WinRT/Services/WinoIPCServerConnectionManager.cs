using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Wino.Domain.Interfaces;

namespace Wino.Shared.WinRT.Services
{
    public class WinoIPCServerConnectionManager : ServerConnectionManagerBase, IWinoServerConnectionManager
    {
        public override Task<bool> ConnectAsync()
        {
            string directory = Path.Combine(Package.Current.InstalledLocation.Path, "Wino.Server", "Wino.Server.exe");

            Process P = new();
            P.StartInfo.UseShellExecute = true;
            P.StartInfo.Verb = "runas";
            P.StartInfo.FileName = directory;

            // TODO: Pass server start arguments with additional options.
            P.StartInfo.Arguments = "";

            return Task.FromResult(P.Start());
        }

        //public Task<bool> DisconnectAsync()
        //{
        //    throw new NotImplementedException();
        //}

        //public void DisposeConnection()
        //{
        //    throw new NotImplementedException();
        //}

        //public void QueueRequest(IRequestBase request, Guid accountId)
        //{
        //    throw new NotImplementedException();
        //}
    }
}
