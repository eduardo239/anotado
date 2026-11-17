using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Anotado;

/// <summary>
/// Vocabulário para o autocompletar: palavras do documento, ordenadas por frequência.
/// </summary>
public sealed class WordCompleter
{
    private const int MinWordLength = 4;
    public const int MinPrefixLength = 2;

    private Dictionary<string, int> _counts = new(StringComparer.Ordinal);

    /// <summary>Reconstrói o vocabulário a partir do texto, ignorando a palavra que está sendo digitada.</summary>
    public void Rebuild(string text, string? wordBeingTyped)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var culture = CultureInfo.GetCultureInfo("pt-BR");
        var skip = wordBeingTyped?.ToLower(culture);
        var skipped = false;

        int i = 0;
        while (i < text.Length)
        {
            if (!IsWordChar(text[i])) { i++; continue; }
            var start = i;
            while (i < text.Length && IsWordChar(text[i])) i++;
            if (i - start < MinWordLength) continue;

            var word = text[start..i].ToLower(culture);
            // A palavra incompleta sob o cursor não deve virar sugestão (conta só uma ocorrência).
            if (!skipped && word == skip) { skipped = true; continue; }
            counts[word] = counts.GetValueOrDefault(word) + 1;
        }
        _counts = counts;
    }

    /// <summary>Retorna o restante da palavra mais relevante que começa com <paramref name="prefix"/>.</summary>
    public string? Suggest(string prefix)
    {
        if (prefix.Length < MinPrefixLength) return null;

        var culture = CultureInfo.GetCultureInfo("pt-BR");
        var key = prefix.ToLower(culture);
        string? best = null;
        var bestCount = 0;
        foreach (var (word, count) in _counts)
        {
            if (word.Length <= key.Length || !word.StartsWith(key, StringComparison.Ordinal)) continue;
            if (count > bestCount || (count == bestCount && word.Length < best!.Length))
            {
                best = word;
                bestCount = count;
            }
        }
        if (best is null) return null;

        var suffix = best[key.Length..];
        // Mantém CAIXA ALTA se o usuário estiver digitando assim.
        return prefix.Length > 1 && prefix.All(c => !char.IsLetter(c) || char.IsUpper(c)) ? suffix.ToUpper(culture) : suffix;
    }

    public static bool IsWordChar(char c) => char.IsLetter(c);
}

/// <summary>Desenha o restante sugerido da palavra, em cinza, logo após o cursor.</summary>
public sealed class SuggestionAdorner : Adorner
{
    private readonly RichTextBox _editor;
    private string? _text;

    public SuggestionAdorner(RichTextBox editor) : base(editor)
    {
        _editor = editor;
        IsHitTestVisible = false;
    }

    public string? Text
    {
        get => _text;
        set { _text = value; InvalidateVisual(); }
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (string.IsNullOrEmpty(_text)) return;

        var caret = _editor.CaretPosition;
        var rect = caret.GetCharacterRect(LogicalDirection.Backward);
        if (rect.IsEmpty) return;

        // Usa a formatação do trecho em que o cursor está.
        var element = caret.Parent as FrameworkContentElement ?? caret.Paragraph;
        var family = (FontFamily?)element?.GetValue(TextElement.FontFamilyProperty) ?? _editor.FontFamily;
        var size = (double?)element?.GetValue(TextElement.FontSizeProperty) ?? _editor.FontSize;
        var style = (FontStyle?)element?.GetValue(TextElement.FontStyleProperty) ?? FontStyles.Normal;
        var weight = (FontWeight?)element?.GetValue(TextElement.FontWeightProperty) ?? FontWeights.Normal;
        var brush = _editor.TryFindResource("PaperMutedBrush") as Brush ?? Brushes.Gray;

        var formatted = new FormattedText(_text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(family, style, weight, FontStretches.Normal), size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        // Alinha pela linha de base do texto, não pelo topo do retângulo do cursor.
        var y = rect.Bottom - formatted.Height;
        dc.PushClip(new RectangleGeometry(new Rect(_editor.RenderSize)));
        dc.DrawText(formatted, new Point(rect.Right, y));
        dc.Pop();
    }
}
