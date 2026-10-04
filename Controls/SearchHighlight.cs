using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using TrayVoiceNotes.Services;

namespace TrayVoiceNotes.Controls;

/// <summary>
/// Attached property that highlights the search terms inside a <see cref="TextBlock"/>, using the
/// block's own highlighters so the text itself is untouched.
/// </summary>
public static class SearchHighlight
{
    public static readonly DependencyProperty QueryProperty = DependencyProperty.RegisterAttached(
        "Query", typeof(string), typeof(SearchHighlight), new PropertyMetadata(string.Empty, OnQueryChanged));

    // Set once the block is watching its own Text, so it isn't hooked twice.
    private static readonly DependencyProperty IsWatchingTextProperty = DependencyProperty.RegisterAttached(
        "IsWatchingText", typeof(bool), typeof(SearchHighlight), new PropertyMetadata(false));

    // A highlighter-pen yellow reads on both the light and dark flyout.
    private static readonly Brush HighlightBackground = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xFF, 0xD5, 0x4F));
    private static readonly Brush HighlightForeground = new SolidColorBrush(Colors.Black);

    public static string GetQuery(DependencyObject element) => (string)element.GetValue(QueryProperty);

    public static void SetQuery(DependencyObject element, string value) => element.SetValue(QueryProperty, value);

    private static void OnQueryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
        {
            return;
        }

        if (!(bool)block.GetValue(IsWatchingTextProperty))
        {
            block.SetValue(IsWatchingTextProperty, true);
            block.RegisterPropertyChangedCallback(TextBlock.TextProperty, (sender, _) => Apply((TextBlock)sender));
        }

        Apply(block);
    }

    private static void Apply(TextBlock block)
    {
        block.TextHighlighters.Clear();
        string query = GetQuery(block);
        if (query.Length == 0 || block.Text.Length == 0)
        {
            return;
        }

        TextHighlighter highlighter = new() { Background = HighlightBackground, Foreground = HighlightForeground };
        foreach ((int start, int length) in NoteSearch.FindRanges(block.Text, query))
        {
            highlighter.Ranges.Add(new TextRange { StartIndex = start, Length = length });
        }

        if (highlighter.Ranges.Count > 0)
        {
            block.TextHighlighters.Add(highlighter);
        }
    }
}
