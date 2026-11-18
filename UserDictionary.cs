using System.Globalization;
using System.IO;
using System.Text;

namespace Anotado;

/// <summary>
/// Dicionário pessoal do corretor ortográfico: palavras que o usuário marcou como corretas.
/// Fica em %APPDATA%\Anotado\dicionario.txt (uma palavra por linha; pode ser editado à mão).
/// </summary>
/// <remarks>
/// Não usa os dicionários personalizados do WPF (SpellCheck.CustomDictionaries): eles são registrados
/// no corretor do Windows para o usuário inteiro, ficam para trás se o app fecha à força e só passam
/// a valer algum tempo depois. Em vez disso, o editor ignora (IgnoreAll) os erros que estão nesta lista.
/// </remarks>
public static class UserDictionary
{
    public static string FilePath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Anotado", "dicionario.txt");

    private static readonly Encoding FileEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
    private static readonly StringComparer Comparer = StringComparer.Create(CultureInfo.GetCultureInfo("pt-BR"), ignoreCase: true);

    private static HashSet<string> _words = new(Comparer);

    /// <summary>Data da última gravação lida, para perceber edições feitas fora do app.</summary>
    public static DateTime LoadedAt { get; private set; }

    public static DateTime LastWrite => File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;

    public static bool Contains(string word) => _words.Contains(word.Trim());

    public static void Load()
    {
        var words = new HashSet<string>(Comparer);
        try
        {
            if (File.Exists(FilePath))
                foreach (var line in File.ReadAllLines(FilePath, FileEncoding))
                    if (line.Trim() is { Length: > 0 } word && !word.StartsWith('#'))
                        words.Add(word);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _words = words;
        LoadedAt = LastWrite;
    }

    /// <summary>Garante que o arquivo exista (para poder abri-lo no Bloco de Notas).</summary>
    public static bool EnsureExists()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, "", FileEncoding);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Acrescenta a palavra, se ainda não estiver lá. Retorna false se não conseguiu gravar.</summary>
    public static bool Add(string word)
    {
        word = word.Trim();
        if (word.Length == 0) return false;
        if (_words.Contains(word)) return true;
        if (!EnsureExists()) return false;

        try
        {
            // Garante que a palavra comece numa linha nova, mesmo se o arquivo foi editado à mão.
            var text = File.ReadAllText(FilePath, FileEncoding);
            var prefix = text.Length == 0 || text.EndsWith('\n') ? "" : Environment.NewLine;
            File.AppendAllText(FilePath, prefix + word + Environment.NewLine, FileEncoding);
            _words.Add(word);
            LoadedAt = LastWrite;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
