using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace EverySearch.Models;

/// <summary>One row in the results list: a file, a folder, or a palette command.</summary>
public sealed class ResultItem : INotifyPropertyChanged
{
    private static readonly Brush Clear = new SolidColorBrush(Colors.Transparent);
    private bool selected;

    public string Name { get; init; } = "";
    public string Dir { get; init; } = "";
    public string FullPath { get; init; } = "";
    public string Glyph { get; init; } = "";
    public string SizeText { get; init; } = "";
    public string DateText { get; init; } = "";
    public bool IsDir { get; init; }
    /// <summary>The type icon for files and folders; null for palette commands, which use <see cref="Glyph"/> instead.</summary>
    public Microsoft.UI.Xaml.Media.ImageSource? Icon { get; init; }
    public string TypeLabel { get; init; } = "";
    /// <summary>Words of the query to emphasise in <see cref="Name"/>.</summary>
    public string[] Terms { get; init; } = Array.Empty<string>();
    public Visibility IconVisibility => Icon != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GlyphVisibility => Icon == null ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>Set for palette commands (rows that run something instead of opening a path).</summary>
    public Action? Command { get; init; }

    public bool IsSelected
    {
        get => selected;
        set
        {
            if (selected == value) return;
            selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RowBackground)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BarVisibility)));
        }
    }

    // The brush instance is shared with Palette, so a theme switch recolours it in place.
    public Brush RowBackground => selected ? (Brush)Application.Current.Resources["AccentTintBrush"] : Clear;
    public Visibility BarVisibility => selected ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{v:0.#} {units[u]}";
    }
}
