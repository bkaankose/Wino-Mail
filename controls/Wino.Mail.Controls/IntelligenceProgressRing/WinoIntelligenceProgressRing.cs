using System.Diagnostics;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Wino.Mail.Controls.IntelligenceProgressRing;

public enum WinoIntelligenceProgressAnimation
{
    Dots,
    Cubes,
    Translate,
    Summarize,
    Rewrite,
}

/// <summary>
/// Displays a compact, looping composition visual for Wino Intelligence operations.
/// </summary>
public sealed partial class WinoIntelligenceProgressRing : Control
{
    private readonly UISettings _uiSettings = new();
    private AnimatedVisualPlayer? _dotsPlayer;
    private AnimatedVisualPlayer? _cubesPlayer;
    private AnimatedVisualPlayer? _translatePlayer;
    private AnimatedVisualPlayer? _summarizePlayer;
    private AnimatedVisualPlayer? _rewritePlayer;
    private int _playVersion;

    [GeneratedDependencyProperty(DefaultValue = WinoIntelligenceProgressAnimation.Dots)]
    public partial WinoIntelligenceProgressAnimation Animation { get; set; }

    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool IsActive { get; set; }

    public WinoIntelligenceProgressRing()
    {
        DefaultStyleKey = typeof(WinoIntelligenceProgressRing);
        RegisterPropertyChangedCallback(AnimationProperty, OnPresentationPropertyChanged);
        RegisterPropertyChangedCallback(IsActiveProperty, OnPresentationPropertyChanged);
        RegisterPropertyChangedCallback(ForegroundProperty, OnForegroundChanged);
    }

    protected override void OnApplyTemplate()
    {
        StopAnimation();
        base.OnApplyTemplate();

        _dotsPlayer = GetTemplateChild("DotsPlayer") as AnimatedVisualPlayer;
        _cubesPlayer = GetTemplateChild("CubesPlayer") as AnimatedVisualPlayer;
        _translatePlayer = GetTemplateChild("TranslatePlayer") as AnimatedVisualPlayer;
        _summarizePlayer = GetTemplateChild("SummarizePlayer") as AnimatedVisualPlayer;
        _rewritePlayer = GetTemplateChild("RewritePlayer") as AnimatedVisualPlayer;

        UpdateSources();
        UpdatePresentation();
    }

    private IEnumerable<AnimatedVisualPlayer?> Players
    {
        get
        {
            yield return _dotsPlayer;
            yield return _cubesPlayer;
            yield return _translatePlayer;
            yield return _summarizePlayer;
            yield return _rewritePlayer;
        }
    }

    private void OnPresentationPropertyChanged(DependencyObject sender, DependencyProperty property) =>
        UpdatePresentation();

    private void OnForegroundChanged(DependencyObject sender, DependencyProperty property)
    {
        UpdateSources();
        UpdatePresentation();
    }

    private void UpdateSources()
    {
        var color = ResolveForegroundColor();

        if (_dotsPlayer is not null)
        {
            _dotsPlayer.Source = new OrbitDotsAnimatedVisualSource();
        }

        if (_cubesPlayer is not null)
        {
            _cubesPlayer.Source = new CubesAnimatedVisualSource(color);
        }

        if (_translatePlayer is not null)
        {
            _translatePlayer.Source = new TranslateAnimatedVisualSource(color);
        }

        if (_summarizePlayer is not null)
        {
            _summarizePlayer.Source = new SummarizeAnimatedVisualSource(color);
        }

        if (_rewritePlayer is not null)
        {
            _rewritePlayer.Source = new RewriteAnimatedVisualSource(color);
        }
    }

    private void UpdatePresentation()
    {
        var selectedPlayer = GetSelectedPlayer();
        if (selectedPlayer is null)
        {
            return;
        }

        StopAnimation();

        foreach (var player in Players)
        {
            if (player is not null)
            {
                player.Visibility = ReferenceEquals(player, selectedPlayer) ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        if (IsActive && _uiSettings.AnimationsEnabled)
        {
            _ = PlaySelectedAsync(selectedPlayer, ++_playVersion);
        }
    }

    private AnimatedVisualPlayer? GetSelectedPlayer() => Animation switch
    {
        WinoIntelligenceProgressAnimation.Dots => _dotsPlayer,
        WinoIntelligenceProgressAnimation.Cubes => _cubesPlayer,
        WinoIntelligenceProgressAnimation.Translate => _translatePlayer,
        WinoIntelligenceProgressAnimation.Summarize => _summarizePlayer,
        WinoIntelligenceProgressAnimation.Rewrite => _rewritePlayer,
        _ => _dotsPlayer,
    };

    private async Task PlaySelectedAsync(AnimatedVisualPlayer player, int playVersion)
    {
        try
        {
            await player.PlayAsync(0, 1, true);
        }
        catch (Exception exception)
        {
            if (playVersion == _playVersion)
            {
                Debug.WriteLine($"Unable to play {Animation} animation: {exception}");
            }
        }
    }

    private void StopAnimation()
    {
        _playVersion++;

        foreach (var player in Players)
        {
            player?.Stop();
        }
    }

    private Color ResolveForegroundColor() =>
        Foreground is SolidColorBrush solidColorBrush
            ? solidColorBrush.Color
            : _uiSettings.GetColorValue(UIColorType.Accent);
}
