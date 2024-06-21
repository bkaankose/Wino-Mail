using Microsoft.Toolkit.Uwp.Notifications;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Background;

namespace Wino.BackgroundTasks
{
    /// <summary>
    /// Creates a toast notification to notify user when the Store update happens.
    /// </summary>
    public sealed class AppUpdatedTask : IBackgroundTask
    {
        public void Run(IBackgroundTaskInstance taskInstance)
        {
            var def = taskInstance.GetDeferral();

            var builder = new ToastContentBuilder();
            builder.SetToastScenario(ToastScenario.Default);

            Package package = Package.Current;
            PackageId packageId = package.Id;
            PackageVersion version = packageId.Version;

            var versionText = string.Format("{0}.{1}.{2}.{3}", version.Major, version.Minor, version.Build, version.Revision);

            // TODO: Handle with Translator, but it's not initialized here yet.
            builder.AddText("Wino Mail is updated!");
            builder.AddText(string.Format("New version {0} is ready.", versionText));

            builder.Show();

            def.Complete();
        }
    }
}
