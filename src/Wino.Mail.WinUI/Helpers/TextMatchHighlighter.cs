using System;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace Wino.Mail.WinUI.Helpers;

/// <summary>
/// Shows a TextBlock's text with the part that matches a query in semibold, the way search
/// suggestions mark what the user typed. Weight carries the emphasis, so no brush is resolved
/// in code and the text follows the element's theme.
/// </summary>
public static class TextMatchHighlighter
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(TextMatchHighlighter), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty QueryProperty = DependencyProperty.RegisterAttached(
        "Query", typeof(string), typeof(TextMatchHighlighter), new PropertyMetadata(null, OnChanged));

    public static string GetText(TextBlock element) => (string)element.GetValue(TextProperty);
    public static void SetText(TextBlock element, string value) => element.SetValue(TextProperty, value);

    public static string GetQuery(TextBlock element) => (string)element.GetValue(QueryProperty);
    public static void SetQuery(TextBlock element, string value) => element.SetValue(QueryProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock textBlock)
            return;

        var text = GetText(textBlock) ?? string.Empty;
        var query = GetQuery(textBlock)?.Trim();

        textBlock.Inlines.Clear();

        var index = FindMatch(text, query);
        if (index < 0)
        {
            textBlock.Inlines.Add(new Run { Text = text });
            return;
        }

        if (index > 0)
            textBlock.Inlines.Add(new Run { Text = text[..index] });

        textBlock.Inlines.Add(new Run { Text = text.Substring(index, query!.Length), FontWeight = FontWeights.SemiBold });

        if (index + query.Length < text.Length)
            textBlock.Inlines.Add(new Run { Text = text[(index + query.Length)..] });
    }

    // Prefers a match at the start of a word, so "an" marks "Anna" in "Joanne Anna" rather than "Joanne".
    private static int FindMatch(string text, string? query)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query))
            return -1;

        var first = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        for (var index = first; index >= 0; index = text.IndexOf(query, index + 1, StringComparison.OrdinalIgnoreCase))
        {
            if (index == 0 || !char.IsLetterOrDigit(text[index - 1]))
                return index;
        }

        return first;
    }
}
