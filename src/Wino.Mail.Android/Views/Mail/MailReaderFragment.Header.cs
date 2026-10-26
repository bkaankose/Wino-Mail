using System.ComponentModel;
using Android.Content;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using CommunityToolkit.Mvvm.Messaging;
using Google.Android.Material.Button;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.Controls.Core.IntelligenceHeader;
using Wino.Mail.Controls.Core.IntelligenceTileBar;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Intelligence;
using Wino.Messaging.Client.Shell;
using Wino.Presentation.Android;
using PopupMenu = AndroidX.AppCompat.Widget.PopupMenu;

namespace Wino.Mail.Android.Views.Mail;

/// <summary>
/// The reader header's extras that the Mac and iOS readers bind: Unsubscribe, Copy address on the sender and the
/// recipients (CopyClipboardCommand), and a small Wino Intelligence card over the shared
/// <see cref="WinoIntelligenceHeaderPresenter"/> (insights, tiles, verification code, processing and summary). The
/// translation panel of the other readers is not built here. S/MIME indicators stay hidden: the platform capabilities
/// declare no S/MIME on Android.
/// </summary>
public sealed partial class MailReaderFragment
{
    private MaterialButton? _unsubscribe;
    private WinoIntelligenceHeaderPresenter? _intelligence;
    private LinearLayout? _intelligenceCard;
    private bool _intelligenceDisposed;

    private void BuildHeaderExtras(View view, Context context, MailRenderingPageViewModel viewModel, IDispatcher dispatcher, IServiceProvider services)
    {
        if (_chips?.Parent is LinearLayout header)
        {
            _unsubscribe = new MaterialButton(context, null, Resource.Attribute.borderlessButtonStyle)
            {
                Text = Translator.Unsubscribe,
                Visibility = ViewStates.Gone
            };
            _unsubscribe.Click += (_, _) => Observe(ViewModel?.UnsubscribeCommand.ExecuteAsync(null));
            header.AddView(_unsubscribe, header.IndexOfChild(_chips),
                new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

            _intelligenceCard = new LinearLayout(context) { Orientation = Orientation.Vertical, Visibility = ViewStates.Gone };
            int padding = WinoTokens.Px(context, Resource.Dimension.wino_space_m);
            _intelligenceCard.SetPadding(padding, padding, padding, padding);
            var fill = new GradientDrawable();
            fill.SetColor(WinoColors.SubtleFill(context));
            fill.SetCornerRadius(WinoUnits.DpF(context, 12));
            _intelligenceCard.Background = fill;
            var cardMargins = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
            {
                TopMargin = WinoTokens.Px(context, Resource.Dimension.wino_space_m)
            };
            header.AddView(_intelligenceCard, header.IndexOfChild(_chips) + 1, cardMargins);
        }

        Bind(viewModel, dispatcher, nameof(MailRenderingPageViewModel.CanUnsubscribe), vm => vm.CanUnsubscribe, canUnsubscribe =>
        {
            if (_unsubscribe is not null) _unsubscribe.Visibility = canUnsubscribe ? ViewStates.Visible : ViewStates.Gone;
        });

        if (view.FindViewById(Resource.Id.reader_sender_row) is { } senderRow)
        {
            senderRow.LongClickable = true;
            senderRow.LongClick += (_, _) => ShowCopyMenu(senderRow, [ViewModel?.FromAddress]);
        }
        if (_recipients is not null)
        {
            _recipients.LongClickable = true;
            _recipients.LongClick += (_, _) =>
            {
                if (ViewModel is not { } current) return;
                ShowCopyMenu(_recipients, current.ToItems.Concat(current.CcItems).Select(static contact => contact.Address).ToArray());
            };
        }

        BuildIntelligence(services);
    }

    /// <summary>"Copy address" for one address, or one entry per address when there are several.</summary>
    private void ShowCopyMenu(View anchor, IReadOnlyList<string?> addresses)
    {
        var distinct = addresses.Where(static address => !string.IsNullOrWhiteSpace(address)).Select(static address => address!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (distinct.Length == 0 || anchor.Context is not { } context) return;
        var popup = new PopupMenu(context, anchor);
        for (int index = 0; index < distinct.Length; index++)
            popup.Menu.Add(0, index, index, distinct.Length == 1 ? Translator.MacOS_Reader_CopyAddress : $"{Translator.MacOS_Reader_CopyAddress}: {distinct[index]}");
        popup.MenuItemClick += (_, args) =>
        {
            if (args.Item?.ItemId is int id && id >= 0 && id < distinct.Length) Observe(ViewModel?.CopyClipboardCommand.ExecuteAsync(distinct[id]));
        };
        popup.Show();
    }

    // ---- Wino Intelligence ----

    private void BuildIntelligence(IServiceProvider services)
    {
        if (_intelligenceCard is null) return;
        var presenter = _intelligence = services.GetRequiredService<WinoIntelligenceHeaderPresenter>();
        _intelligenceDisposed = false;
        presenter.PropertyChanged += IntelligencePropertyChanged;
        WeakReferenceMessenger.Default.Register<MailReaderFragment, LanguageChanged>(this, static (fragment, _) => fragment._intelligence?.OnLanguageChanged());
        presenter.Attach();
        presenter.ResetContent();
        presenter.BeginMailItem(CurrentItem);
        Bindings.Own(new ActionDisposable(DisposeIntelligence));
        SyncIntelligence();
    }

    private async Task IntelligenceHtmlRenderedAsync(string html)
    {
        if (_intelligence is not { } presenter || _intelligenceDisposed || ViewModel is not { } viewModel) return;
        try
        {
            await presenter.OnHtmlRendered(html, viewModel.Subject, viewModel.FromAddress, viewModel.CreationDate);
        }
        catch (Exception exception)
        {
            // The intelligence context is optional; a failure never blocks the message body.
            ReportError(exception);
        }
    }

    private void IntelligenceContentCleared()
    {
        if (_intelligence is { } presenter && !_intelligenceDisposed) presenter.ResetContent();
    }

    private void DisposeIntelligence()
    {
        if (_intelligenceDisposed) return;
        _intelligenceDisposed = true;
        WeakReferenceMessenger.Default.Unregister<LanguageChanged>(this);
        if (_intelligence is { } presenter)
        {
            presenter.PropertyChanged -= IntelligencePropertyChanged;
            presenter.Dispose();
        }
        _intelligence = null;
        _intelligenceCard = null;
    }

    private void IntelligencePropertyChanged(object? sender, PropertyChangedEventArgs args)
        => _ = Entry?.Dispatcher.ExecuteOnUIThread(SyncIntelligence);

    private void SyncIntelligence()
    {
        if (_intelligenceDisposed || _intelligence is not { } presenter || _intelligenceCard is not { } card || card.Context is not { } context) return;
        var state = new WinoIntelligenceHeaderState(
            presenter.ContentKey ?? string.Empty,
            presenter.ProcessingState,
            presenter.IsSummaryAvailable,
            presenter.IsTranslateAvailable,
            presenter.IsProcessingAvailable,
            presenter.BriefingFactText ?? string.Empty,
            presenter.DeadlineText ?? string.Empty,
            presenter.DeadlineDetailText ?? string.Empty,
            presenter.VerificationCode ?? string.Empty,
            presenter.IntelligenceTiles ?? Array.Empty<WinoIntelligenceTile>(),
            presenter.SummaryText ?? string.Empty,
            presenter.SummaryState,
            presenter.TranslationLanguages,
            presenter.SelectedSourceLanguage ?? string.Empty,
            presenter.SelectedTargetLanguage ?? string.Empty,
            presenter.IsTranslationBusy,
            presenter.HasTranslationResult,
            presenter.IsTranslationApplied,
            presenter.TranslationStatusText ?? string.Empty);

        card.RemoveAllViews();
        card.AddView(Text(context, Translator.WinoIntelligence_HeaderTitle, Resource.Style.TextAppearance_WinoKit_BodyStrong));
        if (state.HasBriefingFact) card.AddView(Text(context, state.BriefingFactText, Resource.Style.TextAppearance_WinoKit_Body));
        if (state.HasDeadline)
        {
            card.AddView(Text(context, state.DeadlineText, Resource.Style.TextAppearance_WinoKit_Body));
            if (!string.IsNullOrWhiteSpace(state.DeadlineDetailText))
                card.AddView(Text(context, state.DeadlineDetailText, Resource.Style.TextAppearance_WinoKit_BodySecondary));
        }
        AddTiles(context, card, state);
        if (!string.IsNullOrWhiteSpace(state.VerificationCode))
        {
            card.AddView(Text(context, state.VerificationCode, Resource.Style.TextAppearance_WinoKit_BodyLargeStrong));
            card.AddView(Button(context, Translator.Intelligence_ActionCopyCode, true, () => Observe(presenter.CopyVerificationCodeCommand.ExecuteAsync(null))));
        }
        if (state.CanRequestProcessing || state.IsProcessingRunning)
            card.AddView(Button(context, Translator.WinoIntelligence_Process, state.CanRequestProcessing, () => Observe(presenter.RequestProcessingCommand.ExecuteAsync(null))));
        if (state.IsSummaryAvailable) AddSummary(context, card, presenter, state);

        card.Visibility = presenter.IsVisible && card.ChildCount > 1 ? ViewStates.Visible : ViewStates.Gone;
    }

    private static void AddTiles(Context context, LinearLayout card, WinoIntelligenceHeaderState state)
    {
        if (state.Tiles.Count == 0) return;
        var row = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        foreach (var tile in state.Tiles)
        {
            var text = string.IsNullOrWhiteSpace(tile.Text) ? tile.AccessibleText : tile.Text;
            if (string.IsNullOrWhiteSpace(text)) continue;
            var chip = new WinoChipView(context)
            {
                Text = text,
                ContentDescription = tile.AccessibleText,
                MaxTextWidth = WinoTokens.Dp(context, Resource.Dimension.wino_size_field),
                ChipHeight = WinoTokens.Dp(context, Resource.Dimension.wino_size_swatch_small),
            };
            var fill = tile.IsWarning ? WinoColors.Caution(context) : WinoColors.CardFill(context);
            chip.Fill = fill;
            chip.TextColor = WinoColors.ReadableText(fill);
            row.AddView(chip, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
            {
                RightMargin = WinoTokens.Px(context, Resource.Dimension.wino_space_xs)
            });
        }
        if (row.ChildCount == 0) return;
        var scroll = new HorizontalScrollView(context) { HorizontalScrollBarEnabled = false };
        scroll.AddView(row);
        card.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            TopMargin = WinoTokens.Px(context, Resource.Dimension.wino_space_xs)
        });
    }

    private void AddSummary(Context context, LinearLayout card, WinoIntelligenceHeaderPresenter presenter, WinoIntelligenceHeaderState state)
    {
        switch (state.SummaryState)
        {
            case WinoIntelligenceFeatureState.Busy:
                card.AddView(Text(context, Translator.WinoIntelligence_Summarizing, Resource.Style.TextAppearance_WinoKit_BodySecondary));
                card.AddView(Button(context, Translator.Buttons_Cancel, true, presenter.CancelSummary));
                break;
            case WinoIntelligenceFeatureState.Done when !string.IsNullOrWhiteSpace(state.SummaryText):
                card.AddView(Text(context, Translator.WinoIntelligence_Summary, Resource.Style.TextAppearance_WinoKit_CaptionStrong));
                var summary = Text(context, state.SummaryText, Resource.Style.TextAppearance_WinoKit_Body);
                summary.SetTextIsSelectable(true);
                card.AddView(summary);
                card.AddView(Button(context, Translator.WinoIntelligence_Regenerate, true, () => Observe(presenter.RequestSummaryCommand.ExecuteAsync(null))));
                break;
            default:
                card.AddView(Button(context, Translator.WinoIntelligence_Summarize, true, () => Observe(presenter.RequestSummaryCommand.ExecuteAsync(null))));
                break;
        }
    }

    private static TextView Text(Context context, string text, int style)
    {
        var view = new TextView(context) { Text = text };
        view.SetTextAppearance(style);
        view.SetPadding(0, WinoTokens.Px(context, Resource.Dimension.wino_space_2xs), 0, 0);
        return view;
    }

    private static MaterialButton Button(Context context, string text, bool enabled, Action clicked)
    {
        var button = new MaterialButton(context, null, Resource.Attribute.borderlessButtonStyle) { Text = text, Enabled = enabled };
        button.Click += (_, _) => clicked();
        return button;
    }
}
