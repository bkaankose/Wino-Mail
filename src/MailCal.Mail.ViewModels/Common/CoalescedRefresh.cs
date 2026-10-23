using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace MailCal.Core.ViewModels;

/// <summary>
/// Runs a refresh once for a burst of requests. Saving a settings page or importing preferences
/// raises one PreferenceChangedMessage per property; listeners request a refresh for each and
/// this collapses them into a single reload after the burst settles.
/// </summary>
public sealed class CoalescedRefresh(Func<Task> refreshAsync)
{
    private static readonly TimeSpan BurstWindow = TimeSpan.FromMilliseconds(50);

    private readonly Func<Task> _refreshAsync = refreshAsync;
    private int _isPending;

    public async void Request()
    {
        if (Interlocked.Exchange(ref _isPending, 1) == 1)
            return;

        try
        {
            await Task.Delay(BurstWindow).ConfigureAwait(false);
            Interlocked.Exchange(ref _isPending, 0);
            await _refreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _isPending, 0);
            Log.Error(ex, "Failed to refresh after a preference change.");
        }
    }
}
