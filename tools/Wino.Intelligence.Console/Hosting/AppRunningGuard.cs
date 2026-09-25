namespace Wino.Intelligence.ConsoleApp.Hosting;

/// <summary>
/// The console writes to the same databases and synchronization cursors as the app, so it
/// refuses to run next to it. The mutex name mirrors ReleaseIdentity.MailHostMutexName in
/// src/Wino.NotificationHost.Contracts/ReleaseIdentity.cs, which is scoped to the package family.
/// </summary>
internal sealed class AppRunningGuard : IDisposable
{
    private const string ConsoleMutexName = "Local\\WinoIntelligenceConsoleRunning";
    private readonly Mutex _consoleMutex;

    private AppRunningGuard(Mutex consoleMutex) => _consoleMutex = consoleMutex;

    public static string MailHostMutexName(string packageFamilyName)
        => $"Local\\WinoMail.{packageFamilyName}.MailHostRunning";

    public static bool TryAcquire(string packageFamilyName, out AppRunningGuard? guard, out string? error)
    {
        guard = null;
        error = null;

        if (Mutex.TryOpenExisting(MailHostMutexName(packageFamilyName), out var mailHost))
        {
            mailHost.Dispose();
            error = $"Wino Mail ({packageFamilyName}) is running. Close it before using this console: " +
                    "both would write the same database and synchronization cursors.";
            return false;
        }

        var consoleMutex = new Mutex(initiallyOwned: false, ConsoleMutexName, out var createdNew);
        if (!createdNew)
        {
            consoleMutex.Dispose();
            error = "Another Wino Intelligence console is already running.";
            return false;
        }

        guard = new AppRunningGuard(consoleMutex);
        return true;
    }

    // The mutex is only a name marker; holding the handle open is what keeps it alive.
    public void Dispose() => _consoleMutex.Dispose();
}
