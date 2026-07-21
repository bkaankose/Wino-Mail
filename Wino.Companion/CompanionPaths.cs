using Windows.Storage;

namespace Wino.Companion;

internal static class CompanionPaths
{
    public static string LocalData
    {
        get
        {
            try
            {
                return ApplicationData.Current.LocalFolder.Path;
            }
            catch
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Wino Mail",
                    "Companion");
            }
        }
    }
}
