namespace Anotado;

/// <summary>Família de linguagem do bloco de código, escolhida pela extensão do nome do arquivo.</summary>
public enum CodeLanguage { Plain, Markup, CLike, Hash, Sql, Css, Json }

public enum TokenKind { Text, Keyword, String, Comment, Number, Function, Tag, Attribute }

public readonly record struct CodeToken(int Start, int Length, TokenKind Kind);

/// <summary>Estado que passa de uma linha para a próxima (comentários e tags que continuam).</summary>
public enum CodeLineState { None, BlockComment, MarkupComment, InTag }

/// <summary>
/// Realce de sintaxe simples, linha a linha: palavras-chave, textos entre aspas, comentários,
/// números, nomes de função e, em HTML/XML, tags e atributos. Não é um analisador completo.
/// </summary>
public static class CodeHighlighter
{
    private static readonly HashSet<string> CLikeKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "async", "await", "base", "bool", "break", "byte", "case", "catch", "char", "class",
        "const", "continue", "default", "delegate", "do", "double", "else", "enum", "event", "export", "extends",
        "false", "final", "finally", "float", "for", "foreach", "from", "function", "get", "goto", "if",
        "implements", "import", "in", "init", "int", "interface", "internal", "is", "let", "lock", "long",
        "namespace", "new", "null", "object", "of", "operator", "out", "override", "package", "params", "private",
        "protected", "public", "readonly", "record", "ref", "required", "return", "sealed", "set", "short",
        "static", "string", "struct", "super", "switch", "this", "throw", "true", "try", "type", "typeof",
        "uint", "ulong", "undefined", "using", "val", "var", "virtual", "void", "volatile", "when", "where",
        "while", "with", "yield", "fn", "func", "go", "impl", "mut", "pub", "match", "fun", "nil", "echo",
    };

    private static readonly HashSet<string> HashKeywords = new(StringComparer.Ordinal)
    {
        "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del", "elif", "else",
        "except", "False", "finally", "for", "from", "global", "if", "import", "in", "is", "lambda", "None",
        "nonlocal", "not", "or", "pass", "raise", "return", "True", "try", "while", "with", "yield",
        "then", "fi", "do", "done", "case", "esac", "function", "echo", "export", "local", "param", "true", "false", "null",
    };

    private static readonly HashSet<string> SqlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "select", "from", "where", "and", "or", "not", "insert", "into", "values", "update", "set", "delete",
        "create", "table", "drop", "alter", "add", "join", "inner", "left", "right", "outer", "full", "cross", "on",
        "as", "group", "by", "order", "having", "limit", "offset", "top", "distinct", "union", "all", "null", "is",
        "in", "like", "between", "exists", "primary", "key", "foreign", "references", "index", "view", "case",
        "when", "then", "else", "end", "count", "sum", "avg", "min", "max", "asc", "desc", "default",
        "constraint", "unique", "begin", "commit", "rollback", "with", "returning", "int", "integer",
        "varchar", "text", "date", "timestamp", "boolean", "true", "false",
    };

    public static CodeLanguage FromFileName(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        var ext = dot < 0 ? "" : fileName[(dot + 1)..].Trim().ToLowerInvariant();
        return ext switch
        {
            "html" or "htm" or "xml" or "xaml" or "svg" or "vue" or "csproj" or "config" => CodeLanguage.Markup,
            "js" or "jsx" or "mjs" or "ts" or "tsx" or "cs" or "java" or "c" or "h" or "cpp" or "hpp" or "go"
                or "rs" or "php" or "kt" or "swift" or "dart" or "scala" => CodeLanguage.CLike,
            "py" or "sh" or "bash" or "zsh" or "ps1" or "rb" or "yaml" or "yml" or "toml" or "r" => CodeLanguage.Hash,
            "sql" => CodeLanguage.Sql,
            "css" or "scss" or "less" => CodeLanguage.Css,
            "json" => CodeLanguage.Json,
            _ => CodeLanguage.Plain,
        };
    }

    /// <summary>Tokens coloridos de uma linha (o que não está na lista é texto comum).</summary>
    public static List<CodeToken> Tokenize(string line, CodeLanguage language, ref CodeLineState state)
    {
        var tokens = new List<CodeToken>();
        switch (language)
        {
            case CodeLanguage.Markup: TokenizeMarkup(line, tokens, ref state); break;
            case CodeLanguage.Css: TokenizeCss(line, tokens, ref state); break;
            case CodeLanguage.Plain: break;
            default: TokenizeCode(line, language, tokens, ref state); break;
        }
        return tokens;
    }

    // ---------------------------------------------------------------- C-like, Python/Shell, SQL, JSON

    private static void TokenizeCode(string s, CodeLanguage language, List<CodeToken> tokens, ref CodeLineState state)
    {
        var blockComments = language is CodeLanguage.CLike or CodeLanguage.Sql;
        var i = 0;
        while (i < s.Length)
        {
            if (state == CodeLineState.BlockComment)
            {
                var close = s.IndexOf("*/", i, StringComparison.Ordinal);
                var end = close < 0 ? s.Length : close + 2;
                tokens.Add(new(i, end - i, TokenKind.Comment));
                if (close >= 0) state = CodeLineState.None;
                i = end;
                continue;
            }

            var c = s[i];
            if (IsLineComment(s, i, language))
            {
                tokens.Add(new(i, s.Length - i, TokenKind.Comment));
                return;
            }
            if (blockComments && At(s, i, "/*"))
            {
                state = CodeLineState.BlockComment;
                continue;
            }
            if (c is '"' or '\'' || (c == '`' && language == CodeLanguage.CLike))
            {
                var end = StringEnd(s, i);
                var kind = TokenKind.String;
                if (language == CodeLanguage.Json && NextNonSpace(s, end) == ':')
                    kind = TokenKind.Attribute; // chave do JSON
                tokens.Add(new(i, end - i, kind));
                i = end;
                continue;
            }
            if (char.IsDigit(c))
            {
                var end = i;
                while (end < s.Length && (char.IsLetterOrDigit(s[end]) || s[end] is '.' or '_')) end++;
                tokens.Add(new(i, end - i, TokenKind.Number));
                i = end;
                continue;
            }
            if (char.IsLetter(c) || c is '_' or '$' or '@')
            {
                var end = i + 1;
                while (end < s.Length && (char.IsLetterOrDigit(s[end]) || s[end] == '_')) end++;
                var word = s[i..end];
                var keyword = language switch
                {
                    CodeLanguage.Sql => SqlKeywords.Contains(word),
                    CodeLanguage.Hash => HashKeywords.Contains(word),
                    CodeLanguage.Json => word is "true" or "false" or "null",
                    _ => CLikeKeywords.Contains(word),
                };
                if (keyword)
                    tokens.Add(new(i, end - i, TokenKind.Keyword));
                else if (language != CodeLanguage.Json && NextNonSpace(s, end) == '(')
                    tokens.Add(new(i, end - i, TokenKind.Function));
                i = end;
                continue;
            }
            i++;
        }
    }

    private static bool IsLineComment(string s, int i, CodeLanguage language) => language switch
    {
        CodeLanguage.CLike => At(s, i, "//"),
        CodeLanguage.Hash => s[i] == '#',
        CodeLanguage.Sql => At(s, i, "--"),
        _ => false,
    };

    // ---------------------------------------------------------------- CSS

    private static void TokenizeCss(string s, List<CodeToken> tokens, ref CodeLineState state)
    {
        var selectorLine = s.TrimEnd().EndsWith('{') || (!s.Contains(':') && !s.Contains('}'));
        var i = 0;
        while (i < s.Length)
        {
            if (state == CodeLineState.BlockComment || At(s, i, "/*"))
            {
                var close = s.IndexOf("*/", state == CodeLineState.BlockComment ? i : i + 2, StringComparison.Ordinal);
                var end = close < 0 ? s.Length : close + 2;
                tokens.Add(new(i, end - i, TokenKind.Comment));
                state = close < 0 ? CodeLineState.BlockComment : CodeLineState.None;
                i = end;
                continue;
            }

            var c = s[i];
            if (c is '"' or '\'')
            {
                var end = StringEnd(s, i);
                tokens.Add(new(i, end - i, TokenKind.String));
                i = end;
                continue;
            }
            if (c == '#' && !selectorLine || char.IsDigit(c))
            {
                var end = i + 1;
                while (end < s.Length && (char.IsLetterOrDigit(s[end]) || s[end] is '.' or '%')) end++;
                tokens.Add(new(i, end - i, TokenKind.Number));
                i = end;
                continue;
            }
            if (char.IsLetter(c) || c is '-' or '_' or '@' or '.' or '#' or ':')
            {
                var end = i + 1;
                while (end < s.Length && (char.IsLetterOrDigit(s[end]) || s[end] is '-' or '_')) end++;
                if (c == '@')
                    tokens.Add(new(i, end - i, TokenKind.Keyword));
                else if (selectorLine)
                    tokens.Add(new(i, end - i, TokenKind.Tag));
                else if (NextNonSpace(s, end) == ':')
                    tokens.Add(new(i, end - i, TokenKind.Attribute)); // propriedade
                i = end;
                continue;
            }
            i++;
        }
    }

    // ---------------------------------------------------------------- HTML / XML

    private static void TokenizeMarkup(string s, List<CodeToken> tokens, ref CodeLineState state)
    {
        var i = 0;
        while (i < s.Length)
        {
            if (state == CodeLineState.MarkupComment)
            {
                var close = s.IndexOf("-->", i, StringComparison.Ordinal);
                var end = close < 0 ? s.Length : close + 3;
                tokens.Add(new(i, end - i, TokenKind.Comment));
                if (close >= 0) state = CodeLineState.None;
                i = end;
                continue;
            }

            if (state == CodeLineState.InTag)
            {
                var c = s[i];
                if (c == '>' || At(s, i, "/>"))
                {
                    state = CodeLineState.None;
                    i += c == '>' ? 1 : 2;
                }
                else if (c is '"' or '\'')
                {
                    var end = StringEnd(s, i);
                    tokens.Add(new(i, end - i, TokenKind.String));
                    i = end;
                }
                else if (char.IsLetter(c) || c is '_' or ':' or '@')
                {
                    var end = i + 1;
                    while (end < s.Length && (char.IsLetterOrDigit(s[end]) || s[end] is '-' or '_' or ':' or '.')) end++;
                    tokens.Add(new(i, end - i, TokenKind.Attribute));
                    i = end;
                }
                else i++;
                continue;
            }

            if (At(s, i, "<!--"))
            {
                state = CodeLineState.MarkupComment;
                continue;
            }
            if (s[i] == '<')
            {
                var start = i + 1;
                if (start < s.Length && s[start] is '/' or '!' or '?') start++;
                var end = start;
                while (end < s.Length && (char.IsLetterOrDigit(s[end]) || s[end] is '-' or '_' or ':' or '.')) end++;
                if (end > start)
                {
                    var doctype = s[i + 1] is '!' or '?';
                    tokens.Add(new(start, end - start, doctype ? TokenKind.Keyword : TokenKind.Tag));
                    state = CodeLineState.InTag;
                    i = end;
                    continue;
                }
            }
            i++;
        }
    }

    // ---------------------------------------------------------------- Auxiliares

    private static bool At(string s, int i, string text) =>
        string.CompareOrdinal(s, i, text, 0, text.Length) == 0;

    /// <summary>Fim do texto entre aspas que começa em <paramref name="start"/> (respeita \").</summary>
    private static int StringEnd(string s, int start)
    {
        var quote = s[start];
        var i = start + 1;
        while (i < s.Length)
        {
            if (s[i] == '\\') { i += 2; continue; }
            if (s[i] == quote) return i + 1;
            i++;
        }
        return s.Length;
    }

    private static char NextNonSpace(string s, int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return i < s.Length ? s[i] : '\0';
    }
}
