using System;
using System.Collections.Generic;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Wino.Core.Domain.Models.Contacts;
using Wino.Mail.ViewModels;

namespace Wino.Mail.WinUI.Views.Mail;

/// <summary>
/// @mentions in the body. The editor finds the query and writes the person, and the ViewModel searches people and
/// decides what the list holds; this page only shows that list under the "@".
/// </summary>
public sealed partial class ComposePage
{
    private const double MentionPopupGap = 4;

    private List<RecipientSuggestion> _mentionItems = [];

    private void SubscribeMentions()
    {
        ViewModel.PropertyChanged -= ViewModel_MentionPropertyChanged;
        ViewModel.PropertyChanged += ViewModel_MentionPropertyChanged;
        ViewModel.Mentions = EditorSession;
    }

    private void UnsubscribeMentions()
    {
        ViewModel.PropertyChanged -= ViewModel_MentionPropertyChanged;
        ViewModel.Mentions = null;
        HideMentionPopup();
    }

    private void ViewModel_MentionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ComposePageViewModel.ActiveMentionQuery))
            ShowMentionList();
        else if (e.PropertyName == nameof(ComposePageViewModel.SelectedMentionIndex))
            SelectMentionItem();
    }

    private void ShowMentionList()
    {
        if (ViewModel.ActiveMentionQuery is not { } query)
        {
            HideMentionPopup();
            return;
        }

        if (IsEditorUnavailable || !ComposeEditor.HasKeyboardFocus)
        {
            ViewModel.CloseMentionList();
            return;
        }

        ShowMentionItems();
        PlaceMentionPopup(query);
        MentionPopup.IsOpen = true;
    }

    // The list takes a List<T>: an items source needs a concrete type that the Release build can marshal.
    private void ShowMentionItems()
    {
        _mentionItems = [.. ViewModel.MentionSuggestions];
        MentionSuggestionList.ItemsSource = _mentionItems;
        SelectMentionItem();
    }

    private void SelectMentionItem()
    {
        var index = ViewModel.SelectedMentionIndex;
        if (index < 0 || index >= _mentionItems.Count)
            return;

        MentionSuggestionList.SelectedIndex = index;
        MentionSuggestionList.ScrollIntoView(_mentionItems[index]);
    }

    private void PlaceMentionPopup(ComposeMentionQuery query)
    {
        var transform = ComposeEditor.TransformToVisual(MentionPopup);
        var editorTopLeft = transform.TransformPoint(new Point(0, 0));
        var editorBottomRight = transform.TransformPoint(new Point(ComposeEditor.ActualWidth, ComposeEditor.ActualHeight));
        var anchor = transform.TransformPoint(new Point(query.AnchorX, query.AnchorY));

        MentionPopupContent.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = MentionPopupContent.DesiredSize;

        var below = anchor.Y + query.AnchorHeight + MentionPopupGap;
        var above = anchor.Y - MentionPopupGap - size.Height;
        MentionPopup.VerticalOffset = below + size.Height > editorBottomRight.Y && above >= editorTopLeft.Y ? above : below;
        MentionPopup.HorizontalOffset = Math.Max(editorTopLeft.X, Math.Min(anchor.X, editorBottomRight.X - size.Width));
    }

    private void HideMentionPopup()
    {
        _mentionItems = [];

        if (!MentionPopup.IsOpen)
            return;

        MentionPopup.IsOpen = false;
        MentionSuggestionList.ItemsSource = null;
    }

    private async void MentionSuggestionClicked(object sender, ItemClickEventArgs e)
    {
        try
        {
            await ViewModel.AcceptMentionAsync(e.ClickedItem as RecipientSuggestion);

            if (!IsEditorUnavailable && !ComposeEditor.HasKeyboardFocus)
                await EditorSession.FocusEditorAsync(false);
        }
        catch (ObjectDisposedException) when (IsEditorUnavailable) { }
        catch (Exception exception)
        {
            _logger.CaptureException(exception, "ComposePage.MentionSuggestionClicked");
        }
    }

    private void RemoveMentionSuggestion(RecipientSuggestion suggestion)
    {
        if (!_mentionItems.Contains(suggestion))
            return;

        ViewModel.RemoveMentionSuggestion(suggestion);

        if (ViewModel.IsMentionListOpen)
            ShowMentionItems();
    }

    [WinRT.DynamicWindowsRuntimeCast(typeof(DependencyObject))]
    private void ComposeEditor_LostFocus(object sender, RoutedEventArgs e)
    {
        // A press inside the list does not take focus, but a screen reader or a stray click may move it there.
        if (FocusManager.GetFocusedElement(XamlRoot) is DependencyObject focused && IsInsideMentionPopup(focused))
            return;

        ViewModel.CloseMentionList();
    }

    private bool IsInsideMentionPopup(DependencyObject element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, MentionPopupContent))
                return true;
        }

        return false;
    }
}
