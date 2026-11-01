using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace EverySearch.Models;

/// <summary>
/// Attached properties that make a TextBlock render <c>Text</c> with every occurrence of <c>Terms</c> in the accent
/// colour, so you can see why a result matched.
/// </summary>
public static class Highlight
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached("Text", typeof(string), typeof(Highlight), new PropertyMetadata("", OnChanged));

    public static readonly DependencyProperty TermsProperty =
        DependencyProperty.RegisterAttached("Terms", typeof(string[]), typeof(Highlight), new PropertyMetadata(null, OnChanged));

    public static string GetText(DependencyObject d) => (string)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string value) => d.SetValue(TextProperty, value);
    public static string[]? GetTerms(DependencyObject d) => (string[]?)d.GetValue(TermsProperty);
    public static void SetTerms(DependencyObject d, string[]? value) => d.SetValue(TermsProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBlock block) Render(block);
    }

    private static void Render(TextBlock block)
    {
        string text = GetText(block) ?? "";
        var terms = GetTerms(block);
        block.Inlines.Clear();

        var marked = new bool[text.Length];
        if (terms != null)
        {
            foreach (var term in terms)
            {
                if (string.IsNullOrEmpty(term)) continue;
                for (int at = text.IndexOf(term, StringComparison.OrdinalIgnoreCase); at >= 0;
                     at = text.IndexOf(term, at + term.Length, StringComparison.OrdinalIgnoreCase))
                    for (int i = at; i < at + term.Length; i++) marked[i] = true;
            }
        }

        int start = 0;
        while (start < text.Length)
        {
            int end = start;
            while (end < text.Length && marked[end] == marked[start]) end++;
            var run = new Run { Text = text[start..end] };
            if (marked[start])
            {
                run.FontWeight = FontWeights.SemiBold;
                run.Foreground = (Brush)Application.Current.Resources["AccentBrush"];
            }
            block.Inlines.Add(run);
            start = end;
        }
    }
}
