using System.Globalization;
using System.IO;

namespace Anotado;

/// <summary>
/// Vocabulário para o autocompletar. Prioridade para as palavras do documento (por frequência);
/// se nenhuma servir, usa a lista embutida das palavras mais comuns do português.
/// </summary>
public sealed class WordCompleter
{
    private const int MinWordLength = 4;
    public const int MinPrefixLength = 2;
    private const string CommonWordsResource = "Anotado.Dict.PalavrasComuns.txt";

    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>Palavras comuns, já em minúsculas, sem repetição, na ordem de frequência do arquivo.</summary>
    private static readonly string[] CommonWords = LoadCommonWords();

    private Dictionary<string, int> _counts = new(StringComparer.Ordinal);

    private static string[] LoadCommonWords()
    {
        using var stream = typeof(WordCompleter).Assembly.GetManifestResourceStream(CommonWordsResource);
        if (stream is null) return [];

        using var reader = new StreamReader(stream);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var words = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            var word = line.Trim().ToLower(Culture);
            // Palavras curtas poupariam só uma letra; entradas com espaço ou símbolo ficam de fora.
            if (word.Length >= MinWordLength && word.All(IsWordChar) && seen.Add(word))
                words.Add(word);
        }
        return [.. words];
    }

    /// <summary>Reconstrói o vocabulário a partir do texto, ignorando a palavra que está sendo digitada.</summary>
    public void Rebuild(string text, string? wordBeingTyped)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var skip = wordBeingTyped?.ToLower(Culture);
        var skipped = false;

        int i = 0;
        while (i < text.Length)
        {
            if (!IsWordChar(text[i])) { i++; continue; }
            var start = i;
            while (i < text.Length && IsWordChar(text[i])) i++;
            if (i - start < MinWordLength) continue;

            var word = text[start..i].ToLower(Culture);
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

        var key = prefix.ToLower(Culture);
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
        // Nada no documento: a primeira palavra comum que serve (a lista já vem por frequência).
        best ??= CommonWords.FirstOrDefault(w => w.Length > key.Length && w.StartsWith(key, StringComparison.Ordinal));
        if (best is null) return null;

        var suffix = best[key.Length..];
        // Mantém CAIXA ALTA se o usuário estiver digitando assim.
        return prefix.Length > 1 && prefix.All(c => !char.IsLetter(c) || char.IsUpper(c)) ? suffix.ToUpper(Culture) : suffix;
    }

    public static bool IsWordChar(char c) => char.IsLetter(c);
}
