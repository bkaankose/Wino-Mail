using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Serilog;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.WhatsNew;

namespace Wino.Core.ViewModels;

public partial class WhatsNewPageViewModel : CoreBaseViewModel
{
    private readonly IWhatsNewService _whatsNewService;

    /// <summary>Every bundled release, newest first.</summary>
    public ObservableCollection<WhatsNewRelease> Releases { get; } = [];

    [ObservableProperty]
    public partial WhatsNewRelease? SelectedRelease { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<WhatsNewFeature> Features { get; set; } = [];

    [ObservableProperty]
    public partial WhatsNewFeature? SelectedFeature { get; set; }

    [ObservableProperty]
    public partial bool HasReleases { get; set; }

    /// <summary>Raised on the UI thread once <see cref="Releases"/> is filled.</summary>
    public event EventHandler? ReleasesLoaded;

    public WhatsNewPageViewModel(IWhatsNewService whatsNewService)
    {
        _whatsNewService = whatsNewService;
    }

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        try
        {
            var releases = await _whatsNewService.GetReleasesAsync().ConfigureAwait(false);

            await ExecuteUIThread(() =>
            {
                Releases.Clear();
                foreach (var release in releases)
                    Releases.Add(release);

                HasReleases = Releases.Count > 0;
                SelectedRelease = HasReleases ? Releases[0] : null;
                ReleasesLoaded?.Invoke(this, EventArgs.Empty);
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load the What's New release notes.");
        }
    }

    partial void OnSelectedReleaseChanged(WhatsNewRelease? value)
    {
        Features = value?.Features ?? [];
        SelectedFeature = Features.Count > 0 ? Features[0] : null;
    }
}
