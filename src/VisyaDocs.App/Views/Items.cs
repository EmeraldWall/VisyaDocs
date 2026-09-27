using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using VisyaDocs.Core;

namespace VisyaDocs.App.Views;

/// <summary>A page thumbnail in the side list. The image is rendered lazily when the item scrolls into view.</summary>
public sealed partial class ThumbnailItem(int index, double width, double height) : ObservableObject
{
    public int Index { get; } = index;
    public string Label { get; } = (index + 1).ToString();
    public double Width { get; } = width;
    public double Height { get; } = height;

    [ObservableProperty]
    public partial ImageSource? Image { get; set; }

    public bool IsRendering { get; set; }
}

/// <summary>A comment or highlight in the comments pane.</summary>
public sealed partial class CommentItem(AnnotationInfo info) : ObservableObject
{
    public AnnotationInfo Info { get; } = info;
    public string Glyph { get; } = info.Kind == AnnotationKind.Highlight ? "" : "";
    public string Heading { get; } = $"{(info.Kind == AnnotationKind.Highlight ? "Highlight" : "Comment")} on page {info.PageIndex + 1}";
    public string Contents { get; } = info.Contents.Length > 0 ? info.Contents : "(no text)";
    public string Author { get; } = info.Author;
}
