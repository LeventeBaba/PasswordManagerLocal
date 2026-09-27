using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;

namespace PasswordManagerLocal.Common.Frontend.Views.Controls;

public sealed class LinkifiedTextBlock : TextBlock
{
    private static readonly Regex UrlRegex = new(
        @"(?<![\p{L}\p{N}_@])(?:https?://|www\.)[^\s<>""']+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly IBrush LinkForeground = Brush.Parse("#FF2D6AE3");
    private static readonly Cursor LinkCursor = new(StandardCursorType.Hand);

    private readonly List<LinkTextRange> _links = [];

    public static readonly StyledProperty<string?> SourceTextProperty =
        AvaloniaProperty.Register<LinkifiedTextBlock, string?>(nameof(SourceText));

    public LinkifiedTextBlock()
    {
        Tapped += HandleTapped;
        PointerMoved += HandlePointerMoved;
        PointerExited += HandlePointerExited;
    }

    public string? SourceText
    {
        get => GetValue(SourceTextProperty);
        set => SetValue(SourceTextProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SourceTextProperty)
        {
            RebuildInlines();
        }
    }

    private void RebuildInlines()
    {
        (Inlines ??= new InlineCollection()).Clear();
        _links.Clear();

        var text = SourceText ?? string.Empty;

        try
        {
            AddParsedInlines(text);
        }
        catch (RegexMatchTimeoutException)
        {
            (Inlines ??= new InlineCollection()).Clear();
            _links.Clear();
            (Inlines ??= new InlineCollection()).Add(new Run(text));
        }
    }

    private void AddParsedInlines(string text)
    {
        var currentIndex = 0;

        foreach (Match match in UrlRegex.Matches(text))
        {
            var urlLength = GetUrlLengthWithoutTrailingPunctuation(match.Value);
            if (urlLength == 0)
            {
                continue;
            }

            if (match.Index > currentIndex)
            {
                (Inlines ??= new InlineCollection()).Add(new Run(text[currentIndex..match.Index]));
            }

            var displayedUrl = match.Value[..urlLength];
            if (TryCreateWebUri(displayedUrl, out var uri))
            {
                AddLinkInline(displayedUrl, match.Index, uri);
            }
            else
            {
                (Inlines ??= new InlineCollection()).Add(new Run(displayedUrl));
            }

            currentIndex = match.Index + urlLength;
        }

        if (currentIndex < text.Length)
        {
            (Inlines ??= new InlineCollection()).Add(new Run(text[currentIndex..]));
        }
    }

    private void AddLinkInline(string displayedUrl, int startIndex, Uri uri)
    {
        (Inlines ??= new InlineCollection()).Add(new Run(displayedUrl)
        {
            Foreground = LinkForeground,
            TextDecorations = Avalonia.Media.TextDecorations.Underline
        });

        _links.Add(new LinkTextRange(startIndex, startIndex + displayedUrl.Length, uri));
    }

    private void HandleTapped(object? sender, TappedEventArgs args)
    {
        var link = GetLinkAtPoint(args.GetPosition(this));
        if (link is null)
        {
            return;
        }

        args.Handled = true;
        _ = LaunchUriAsync(link.Uri);
    }

    private void HandlePointerMoved(object? sender, PointerEventArgs args)
    {
        Cursor = GetLinkAtPoint(args.GetPosition(this)) is null ? null : LinkCursor;
    }

    private void HandlePointerExited(object? sender, PointerEventArgs args)
    {
        Cursor = null;
    }

    private LinkTextRange? GetLinkAtPoint(Point point)
    {
        if (_links.Count == 0 || TextLayout is null)
        {
            return null;
        }

        var hit = TextLayout.HitTestPoint(point);
        if (!hit.IsInside)
        {
            return null;
        }

        var characterIndex = hit.TextPosition;
        if (characterIndex < 0)
        {
            return null;
        }

        return GetLinkContainingCharacter(characterIndex);
    }

    private LinkTextRange? GetLinkContainingCharacter(int characterIndex)
    {
        foreach (var link in _links)
        {
            if (characterIndex >= link.StartIndex && characterIndex < link.EndIndex)
            {
                return link;
            }
        }

        return null;
    }

    private async Task LaunchUriAsync(Uri uri)
    {
        var launcher = TopLevel.GetTopLevel(this)?.Launcher;
        if (launcher is null)
        {
            return;
        }

        try
        {
            await launcher.LaunchUriAsync(uri);
        }
        catch
        {
            // Opening external links depends on the host platform.
            // Ignore launcher failures so a bad OS handler cannot crash the details view.
        }
    }

    private static bool TryCreateWebUri(string displayedUrl, out Uri uri)
    {
        var uriText = displayedUrl.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? $"https://{displayedUrl}"
            : displayedUrl;

        if (Uri.TryCreate(uriText, UriKind.Absolute, out var parsedUri)
            && (parsedUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || parsedUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            uri = parsedUri;
            return true;
        }

        uri = null!;
        return false;
    }

    private static int GetUrlLengthWithoutTrailingPunctuation(string value)
    {
        var length = value.Length;

        while (length > 0)
        {
            var lastCharacter = value[length - 1];

            if (lastCharacter is '.' or ',' or ';' or ':' or '!' or '?')
            {
                length--;
                continue;
            }

            if ((lastCharacter == ')' && HasMoreClosingCharacters(value, length, '(', ')'))
                || (lastCharacter == ']' && HasMoreClosingCharacters(value, length, '[', ']'))
                || (lastCharacter == '}' && HasMoreClosingCharacters(value, length, '{', '}')))
            {
                length--;
                continue;
            }

            break;
        }

        return length;
    }

    private static bool HasMoreClosingCharacters(string value, int length, char opening, char closing)
    {
        var balance = 0;

        for (var index = 0; index < length; index++)
        {
            if (value[index] == opening)
            {
                balance++;
            }
            else if (value[index] == closing)
            {
                balance--;
            }
        }

        return balance < 0;
    }

    private sealed class LinkTextRange(int startIndex, int endIndex, Uri uri)
    {
        public int StartIndex { get; } = startIndex;

        public int EndIndex { get; } = endIndex;

        public Uri Uri { get; } = uri;
    }
}
