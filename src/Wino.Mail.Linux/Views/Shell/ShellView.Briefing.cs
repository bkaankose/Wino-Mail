using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Messaging.UI;
using Wino.Mail.Linux.Infrastructure;

namespace Wino.Mail.Linux.Views.Shell;

/// <summary>
/// The Daily briefing panel in the shell's trailing pane (macOS WinoAppShellViewController ObserveBriefing /
/// ToggleBriefing): the header button, the app action and <see cref="DailyBriefingOpenRequested"/> open it; the panel
/// loads while shown and marks the briefing viewed when it closes. The panel belongs to the presenter and outlives the shell.
/// </summary>
internal sealed partial class ShellView
{
    private Gtk.Widget? _briefingPanel;

    private void WireBriefing()
    {
        _header.BriefingRequested += _ => ToggleBriefing();
        _layout.TrailingPanelShownChanged += BriefingShownChanged;
        Subscribe<DailyBriefingOpenRequested>(_ => OpenBriefing());
    }

    internal void ToggleBriefing()
    {
        if (IsReleased || _briefing is not { IsAvailable: true } presenter) return;
        if (_layout.IsTrailingPanelShown)
        {
            _layout.ShowTrailingPanel(false);
            return;
        }
        _briefingPanel ??= presenter.CreatePanel(() => RunOnUI(() => _layout.ShowTrailingPanel(false)));
        _layout.SetTrailingPanel(_briefingPanel);
        _layout.ShowTrailingPanel(true);
    }

    private void OpenBriefing()
    {
        if (IsReleased || _briefing is not { IsAvailable: true }) return;
        if (_modes.ActiveMode != WinoApplicationMode.Mail && !_navigation.ChangeApplicationMode(WinoApplicationMode.Mail)) return;
        (Root.GetRoot() as Gtk.Window)?.Present();
        // A mode switch settles on the next turn; the panel opens after it.
        Wino.Presentation.Linux.MainLoop.Post(() =>
        {
            if (!IsReleased && !_layout.IsTrailingPanelShown) ToggleBriefing();
        });
    }

    private void BriefingShownChanged(bool shown)
    {
        (_briefing as DailyBriefingPresenter)?.Panel?.SetShown(shown);
        ApplyBriefing();
    }

    private void ReleaseBriefing()
    {
        _layout.TrailingPanelShownChanged -= BriefingShownChanged;
        (_briefing as DailyBriefingPresenter)?.Panel?.SetShown(false);
        // The presenter keeps the panel for the next shell, so it must leave this layout.
        _layout.SetTrailingPanel(null);
        _briefingPanel = null;
    }

#if DEBUG
    internal bool DebugBriefingShown => _layout.IsTrailingPanelShown;

    internal bool DebugBriefingOverlay => _layout.IsTrailingPanelOverlay;
#endif
}
