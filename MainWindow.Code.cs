using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace Anotado;

/// <summary>
/// Blocos de código (estilo "gist"): uma tabela de 1 coluna com cabeçalho (nome do arquivo) e
/// uma linha de código por parágrafo. É feita com elementos do próprio documento, então sobrevive
/// ao RTF; números de linha e botões (Copiar/Remover) ficam numa camada por cima e não vão ao arquivo.
/// </summary>
public partial class MainWindow
{
    // Cores fixas (vão para o arquivo): tema escuro, como no GitHub.
    private static readonly Color CodeBackColor = Color.FromRgb(0x0D, 0x11, 0x17);
    private static readonly Brush CodeBack = Frozen(CodeBackColor);
    private static readonly Brush CodeHeaderBack = Frozen(Color.FromRgb(0x16, 0x1B, 0x22));
    private static readonly Brush CodeBorder = Frozen(Color.FromRgb(0x30, 0x36, 0x3D));
    private static readonly Brush CodeHeaderText = Frozen(Color.FromRgb(0x58, 0xA6, 0xFF));
    private static readonly Brush CodeLineNumber = Frozen(Color.FromRgb(0x6E, 0x76, 0x81));

    private static readonly Dictionary<TokenKind, Brush> TokenBrushes = new()
    {
        [TokenKind.Text] = Frozen(Color.FromRgb(0xE6, 0xED, 0xF3)),
        [TokenKind.Keyword] = Frozen(Color.FromRgb(0xFF, 0x7B, 0x72)),
        [TokenKind.String] = Frozen(Color.FromRgb(0xA5, 0xD6, 0xFF)),
        [TokenKind.Comment] = Frozen(Color.FromRgb(0x8B, 0x94, 0x9E)),
        [TokenKind.Number] = Frozen(Color.FromRgb(0x79, 0xC0, 0xFF)),
        [TokenKind.Function] = Frozen(Color.FromRgb(0xD2, 0xA8, 0xFF)),
        [TokenKind.Tag] = Frozen(Color.FromRgb(0x7E, 0xE7, 0x87)),
        [TokenKind.Attribute] = Frozen(Color.FromRgb(0x79, 0xC0, 0xFF)),
    };

    // Propriedade (não campo): a ordem de inicialização entre arquivos "partial" não é garantida.
    private static FontFamily CodeFamily => PresetFamilies[(int)FontPreset.Monospace];
    private static readonly double CodeFontSize = PtToDip(10);
    private const double CodeGutter = 52; // espaço à esquerda para os números de linha

    /// <summary>"Sem idioma": o corretor ortográfico ignora o código.</summary>
    private static readonly XmlLanguage NoLanguage = XmlLanguage.GetLanguage("zxx");

    private static readonly (string Name, string Extension)[] CodeLanguages =
    [
        ("HTML", "html"), ("CSS", "css"), ("JavaScript", "js"), ("TypeScript", "ts"), ("C#", "cs"),
        ("Java", "java"), ("Python", "py"), ("SQL", "sql"), ("JSON", "json"), ("Shell", "sh"), ("Texto", "txt"),
    ];

    private DispatcherTimer? _codeTimer;
    private readonly List<TextBlock> _lineNumbers = [];
    private readonly List<(Button Copy, Button Remove)> _codeButtons = [];

    // ================================================================ Inserir

    private void InsertCode_Click(object sender, RoutedEventArgs e)
    {
        if (Editor.IsReadOnly) return;

        var list = new StackPanel { Margin = new Thickness(6) };
        var title = new TextBlock { Text = "Linguagem do trecho", FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 4, 8, 6) };
        list.Children.Add(title);

        Popup? popup = null;
        foreach (var (name, extension) in CodeLanguages)
        {
            var item = new Button
            {
                Content = name, Style = (Style)FindResource("IconButton"),
                HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 5, 10, 5), MinWidth = 160,
            };
            item.Click += (_, _) =>
            {
                popup!.IsOpen = false;
                InsertCodeBlock($"trecho.{extension}");
            };
            list.Children.Add(item);
        }

        var frame = new Border { Child = list, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        frame.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        frame.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
        frame.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
        frame.SetResourceReference(TextElement.FontFamilyProperty, "UiFont");

        popup = new Popup
        {
            Child = frame, PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom,
            StaysOpen = false, AllowsTransparency = true, VerticalOffset = 4,
        };
        popup.Opened += (_, _) => (list.Children[1] as Button)?.Focus();
        popup.Closed += (_, _) => { if (!Editor.IsKeyboardFocusWithin) Editor.Focus(); };
        popup.IsOpen = true;
    }

    /// <summary>Insere um bloco de código; o texto selecionado (se houver) vira o conteúdo do bloco.</summary>
    private void InsertCodeBlock(string fileName)
    {
        var selected = Editor.Selection.IsEmpty ? "" : Editor.Selection.Text.TrimEnd('\r', '\n');

        var table = new Table();
        var group = new TableRowGroup();
        var header = new TableRow();
        header.Cells.Add(new TableCell(new Paragraph(new Run(fileName))));
        var code = new TableRow();
        var cell = new TableCell();
        foreach (var line in selected.Length == 0 ? new[] { "" } : selected.Split('\n'))
            cell.Blocks.Add(new Paragraph(new Run(line.TrimEnd('\r'))));
        code.Cells.Add(cell);
        group.Rows.Add(header);
        group.Rows.Add(code);
        table.RowGroups.Add(group);
        StyleCodeBlock(table);
        HighlightCodeBlock(table);

        Editor.BeginChange();
        if (selected.Length > 0)
            Editor.Selection.Text = "";

        // Dentro de tabela ou lista, o bloco vai depois dela (não dentro).
        Block? anchor = Editor.CaretPosition.Paragraph;
        while (anchor is not null && anchor.Parent is not (FlowDocument or Section))
        {
            DependencyObject? up = anchor.Parent;
            while (up is not null and not (Table or List))
                up = LogicalTreeHelper.GetParent(up);
            anchor = up as Block;
        }

        if (anchor?.SiblingBlocks is { } siblings)
        {
            siblings.InsertAfter(anchor, table);
            if (anchor is Paragraph empty && new TextRange(empty.ContentStart, empty.ContentEnd).IsEmpty)
                siblings.Remove(empty); // o bloco ocupa o lugar do parágrafo vazio
        }
        else
        {
            Editor.Document.Blocks.Add(table);
        }
        KeepApartFromTables(table);
        Editor.EndChange();

        var last = (Paragraph)cell.Blocks.LastBlock;
        Editor.CaretPosition = last.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
        Editor.Focus();
    }

    /// <summary>
    /// Garante um parágrafo antes e depois da tabela quando a vizinha também é tabela (e depois,
    /// se ela for a última): no RTF, tabelas encostadas viram uma só ao reabrir o arquivo.
    /// </summary>
    private static void KeepApartFromTables(Table table)
    {
        var siblings = table.SiblingBlocks!;
        if (table.PreviousBlock is Table)
            siblings.InsertBefore(table, new Paragraph());
        if (table.NextBlock is null or Table)
            siblings.InsertAfter(table, new Paragraph());
    }

    // ================================================================ Reconhecer e estilizar

    /// <summary>
    /// Arquivos salvos com blocos de código encostados voltam do RTF como uma única tabela:
    /// separa de novo cada par cabeçalho + código.
    /// </summary>
    private static void SplitMergedCodeBlocks(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks.ToList())
        {
            if (block is Section section)
            {
                SplitMergedCodeBlocks(section.Blocks);
                continue;
            }
            if (block is not Table table)
                continue;

            var rows = table.RowGroups.SelectMany(g => g.Rows).ToList();
            bool IsColor(TableRow row, Brush brush) =>
                row.Cells.Count == 1 && row.Cells[0].Background is SolidColorBrush b && b.Color == ((SolidColorBrush)brush).Color;
            var pairs = rows.Count > 2 && rows.Count % 2 == 0
                        && rows.Select((row, i) => IsColor(row, i % 2 == 0 ? CodeHeaderBack : CodeBack)).All(ok => ok);
            if (!pairs)
                continue;

            Block previous = table;
            for (var i = 2; i < rows.Count; i += 2)
            {
                var group = new TableRowGroup();
                foreach (var row in new[] { rows[i], rows[i + 1] })
                {
                    ((TableRowGroup)row.Parent).Rows.Remove(row);
                    group.Rows.Add(row);
                }
                var next = new Table();
                next.RowGroups.Add(group);
                var separator = new Paragraph();
                table.SiblingBlocks!.InsertAfter(previous, separator);
                table.SiblingBlocks!.InsertAfter(separator, next);
                previous = next;
            }
        }
    }

    /// <summary>Bloco de código = tabela com 2 linhas de 1 célula, a segunda com o fundo do código.</summary>
    private static bool IsCodeBlock(Table table)
    {
        var rows = table.RowGroups.SelectMany(g => g.Rows).ToList();
        return rows.Count == 2 && rows.All(r => r.Cells.Count == 1)
               && rows[1].Cells[0].Background is SolidColorBrush { Color: var color } && color == CodeBackColor;
    }

    private static TableCell HeaderCell(Table table) => table.RowGroups[0].Rows[0].Cells[0];
    private static TableCell CodeCell(Table table) => table.RowGroups.SelectMany(g => g.Rows).ElementAt(1).Cells[0];

    private static string HeaderText(Table table) =>
        new TextRange(HeaderCell(table).ContentStart, HeaderCell(table).ContentEnd).Text.Trim();

    private static IEnumerable<Paragraph> CodeLines(Table table) => CodeCell(table).Blocks.OfType<Paragraph>();

    private static string LineText(Paragraph p) => new TextRange(p.ContentStart, p.ContentEnd).Text;

    private static IEnumerable<Table> CodeBlocks(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Table table when IsCodeBlock(table):
                    yield return table;
                    break;
                case Section section:
                    foreach (var t in CodeBlocks(section.Blocks)) yield return t;
                    break;
                case List list:
                    foreach (var item in list.ListItems)
                        foreach (var t in CodeBlocks(item.Blocks)) yield return t;
                    break;
            }
        }
    }

    /// <summary>O bloco de código onde está o cursor, e se o cursor está no cabeçalho.</summary>
    private (Table Table, bool InHeader)? CurrentCodeBlock()
    {
        DependencyObject? node = Editor.Selection.Start.Parent;
        while (node is not null and not TableCell)
            node = LogicalTreeHelper.GetParent(node);
        if (node is TableCell { Parent: TableRow { Parent: TableRowGroup { Parent: Table table } } row } && IsCodeBlock(table))
            return (table, table.RowGroups.SelectMany(g => g.Rows).First() == row);
        return null;
    }

    /// <summary>
    /// Aplica o visual do bloco (o RTF perde espaçamentos, idioma e a fonte embutida).
    /// Só altera o que estiver diferente, para não gerar mudanças à toa no documento.
    /// </summary>
    private static void StyleCodeBlock(Table table)
    {
        Set(table, Table.CellSpacingProperty, 0.0);
        Set(table, Block.MarginProperty, new Thickness(0, 6, 0, 14));
        // O RTF volta com largura fixa estreita: o bloco sempre ocupa a largura da página.
        if (table.Columns.Count != 1 || !table.Columns[0].Width.IsStar)
        {
            table.Columns.Clear();
            table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        }

        var header = HeaderCell(table);
        Set(header, TableCell.BackgroundProperty, CodeHeaderBack);
        Set(header, TableCell.BorderBrushProperty, CodeBorder);
        Set(header, TableCell.BorderThicknessProperty, new Thickness(1));
        Set(header, TableCell.PaddingProperty, new Thickness(14, 8, 120, 8)); // espaço à direita para os botões
        if (header.Blocks.Count == 0) header.Blocks.Add(new Paragraph());
        foreach (var p in header.Blocks.OfType<Paragraph>())
        {
            Set(p, Block.MarginProperty, new Thickness(0));
            Set(p, TextElement.ForegroundProperty, CodeHeaderText);
            Set(p, TextElement.FontWeightProperty, FontWeights.SemiBold);
            Set(p, TextElement.FontSizeProperty, PtToDip(9.5));
            Set(p, FrameworkContentElement.LanguageProperty, NoLanguage);
            ClearDescendants(p, TextElement.ForegroundProperty, TextElement.FontSizeProperty, FrameworkContentElement.LanguageProperty);
        }

        var code = CodeCell(table);
        Set(code, TableCell.BackgroundProperty, CodeBack);
        Set(code, TableCell.BorderBrushProperty, CodeBorder);
        Set(code, TableCell.BorderThicknessProperty, new Thickness(1, 0, 1, 1));
        Set(code, TableCell.PaddingProperty, new Thickness(CodeGutter, 10, 14, 10));
        if (code.Blocks.Count == 0) code.Blocks.Add(new Paragraph());
        foreach (var p in code.Blocks.OfType<Paragraph>())
        {
            Set(p, Block.MarginProperty, new Thickness(0));
            Set(p, Block.TextAlignmentProperty, TextAlignment.Left);
            Set(p, TextElement.FontFamilyProperty, CodeFamily);
            Set(p, TextElement.FontSizeProperty, CodeFontSize);
            Set(p, TextElement.FontWeightProperty, FontWeights.Normal);
            Set(p, FrameworkContentElement.LanguageProperty, NoLanguage);
            // Sem ligaduras: "<!--" e "-->" aparecem como foram digitados (não como setas).
            Set(p, Typography.StandardLigaturesProperty, false);
            Set(p, Typography.ContextualLigaturesProperty, false);
            Set(p, Typography.ContextualAlternatesProperty, false);
            ClearDescendants(p, TextElement.FontFamilyProperty, TextElement.FontSizeProperty, TextElement.BackgroundProperty,
                FrameworkContentElement.LanguageProperty);
        }
    }

    private static void Set(DependencyObject target, DependencyProperty property, object value)
    {
        var current = target.ReadLocalValue(property);
        var same = (current, value) switch
        {
            (SolidColorBrush a, SolidColorBrush b) => a.Color == b.Color,
            (FontFamily a, FontFamily b) => a.Source == b.Source,
            (XmlLanguage a, XmlLanguage b) => a.IetfLanguageTag == b.IetfLanguageTag,
            _ => Equals(current, value),
        };
        if (!same) target.SetValue(property, value);
    }

    /// <summary>Remove formatação gravada nos trechos internos, para valer a do parágrafo.</summary>
    private static void ClearDescendants(DependencyObject node, params DependencyProperty[] properties)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
        {
            if (child is TextElement element)
                foreach (var property in properties)
                    if (element.ReadLocalValue(property) != DependencyProperty.UnsetValue)
                        element.ClearValue(property);
            ClearDescendants(child, properties);
        }
    }

    /// <summary>Estiliza e colore todos os blocos de código (ao abrir, colar ou imprimir).</summary>
    private static void PrepareCodeBlocks(FlowDocument doc)
    {
        SplitMergedCodeBlocks(doc.Blocks);
        foreach (var table in CodeBlocks(doc.Blocks).ToList())
        {
            StyleCodeBlock(table);
            HighlightCodeBlock(table);
        }
    }

    // ================================================================ Cores de sintaxe

    /// <summary>Colore o bloco linha a linha; só aplica cor onde ela está diferente.</summary>
    private static void HighlightCodeBlock(Table table)
    {
        var language = CodeHighlighter.FromFileName(HeaderText(table));
        var state = CodeLineState.None;

        foreach (var p in CodeLines(table).ToList())
        {
            var text = LineText(p);
            var tokens = CodeHighlighter.Tokenize(text, language, ref state);

            // Trechos sem token ficam com a cor de texto comum.
            var position = 0;
            foreach (var token in tokens.Append(new CodeToken(text.Length, 0, TokenKind.Text)))
            {
                if (token.Start > position)
                    Paint(p, position, token.Start, TokenKind.Text);
                if (token.Length > 0)
                    Paint(p, token.Start, token.Start + token.Length, token.Kind);
                position = Math.Max(position, token.Start + token.Length);
            }
        }
    }

    private static void Paint(Paragraph p, int start, int end, TokenKind kind)
    {
        var range = new TextRange(PositionAt(p, start), PositionAt(p, end));
        if (range.IsEmpty) return;
        var brush = TokenBrushes[kind];
        if (range.GetPropertyValue(TextElement.ForegroundProperty) is SolidColorBrush current
            && current.Color == ((SolidColorBrush)brush).Color)
            return;
        range.ApplyPropertyValue(TextElement.ForegroundProperty, brush);
    }

    /// <summary>Posição no parágrafo depois de <paramref name="offset"/> caracteres de texto.</summary>
    private static TextPointer PositionAt(Paragraph p, int offset)
    {
        var navigator = p.ContentStart;
        while (navigator.CompareTo(p.ContentEnd) < 0)
        {
            if (navigator.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                var length = navigator.GetTextRunLength(LogicalDirection.Forward);
                if (offset <= length)
                    return navigator.GetPositionAtOffset(offset)!;
                offset -= length;
                navigator = navigator.GetPositionAtOffset(length)!;
            }
            else
            {
                navigator = navigator.GetNextContextPosition(LogicalDirection.Forward)!;
            }
        }
        return p.ContentEnd;
    }

    /// <summary>Depois de digitar num bloco, reestiliza e recolore (com uma pequena pausa).</summary>
    private void ScheduleCodeRefresh(TextChangedEventArgs e)
    {
        // Desfazer/refazer não recolore: senão cada Ctrl+Z geraria uma nova mudança.
        if (e.UndoAction is UndoAction.Undo or UndoAction.Redo || CurrentCodeBlock() is null)
            return;

        if (_codeTimer is null)
        {
            _codeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            _codeTimer.Tick += (_, _) =>
            {
                _codeTimer.Stop();
                if (CurrentCodeBlock() is not { } block) return;
                Editor.BeginChange();
                StyleCodeBlock(block.Table);
                HighlightCodeBlock(block.Table);
                Editor.EndChange();
            };
        }
        _codeTimer.Stop();
        _codeTimer.Start();
    }

    // ================================================================ Teclado e colar

    /// <summary>Tab insere espaços; Enter no cabeçalho vai para a primeira linha de código.</summary>
    private bool HandleCodeKey(KeyEventArgs e)
    {
        if (!Editor.IsKeyboardFocusWithin || Editor.IsReadOnly || Keyboard.Modifiers != ModifierKeys.None
            || CurrentCodeBlock() is not { } block)
            return false;

        switch (e.Key)
        {
            case Key.Tab when !block.InHeader:
                var end = Editor.CaretPosition.GetPositionAtOffset(0, LogicalDirection.Forward)!;
                Editor.Selection.Text = "    ";
                Editor.CaretPosition = end;
                return true;
            case Key.Enter when block.InHeader:
                var first = CodeLines(block.Table).FirstOrDefault();
                if (first is not null)
                    Editor.CaretPosition = first.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
                return true;
        }
        return false;
    }

    /// <summary>Dentro do bloco, cola só o texto (sem formatação); no cabeçalho, numa linha só.</summary>
    private void Editor_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (CurrentCodeBlock() is not { } block || !e.DataObject.GetDataPresent(DataFormats.UnicodeText))
            return;

        var text = (e.DataObject.GetData(DataFormats.UnicodeText) as string ?? "").Replace("\r\n", "\n");
        e.CancelCommand();

        Editor.BeginChange();
        if (block.InHeader)
        {
            Editor.Selection.Text = text.Replace('\n', ' ').Trim();
        }
        else
        {
            // Ponteiros com gravidade para frente terminam depois do texto inserido.
            var lines = text.Split('\n');
            var caret = Editor.Selection.Start.GetPositionAtOffset(0, LogicalDirection.Forward)!;
            Editor.Selection.Text = lines[0];
            for (var i = 1; i < lines.Length; i++)
            {
                caret = caret.InsertParagraphBreak().GetPositionAtOffset(0, LogicalDirection.Forward)!;
                caret.InsertTextInRun(lines[i]);
            }
            Editor.CaretPosition = caret;
        }
        StyleCodeBlock(block.Table);
        HighlightCodeBlock(block.Table);
        Editor.EndChange();
    }

    // ================================================================ Números de linha e botões

    private void LayoutCodeBlocks()
    {
        if (Editor.Template.FindName("PART_ContentHost", Editor) is not ScrollViewer host)
            return;

        var numbers = 0;
        var buttons = 0;
        foreach (var table in CodeBlocks(Editor.Document.Blocks))
        {
            var rows = table.RowGroups.SelectMany(g => g.Rows).ToList();
            if (RowBounds(rows[0]) is not { } header || RowBounds(rows[1]) is not { } body)
                continue;
            if (body.Bottom < 0 || header.Top > Editor.ActualHeight)
                continue; // fora da tela

            var left = Editor.Padding.Left + table.Margin.Left;
            var right = Editor.Padding.Left + host.ViewportWidth - table.Margin.Right;

            // Botões no cabeçalho, à direita.
            if (buttons == _codeButtons.Count)
                _codeButtons.Add((MakeCodeButton("Copiar", "Copiar o código", copy: true),
                                  MakeCodeButton("", "Remover o bloco de código", copy: false)));
            var (copy, remove) = _codeButtons[buttons++];
            copy.Tag = remove.Tag = table;
            var middle = (header.Top + header.Bottom) / 2;
            PlaceAt(copy, right - 10 - 64, middle - 12, 64);
            PlaceAt(remove, right - 10 - 64 - 6 - 28, middle - 12, 28);

            // Números de linha, alinhados ao topo de cada parágrafo.
            var n = 0;
            foreach (var p in CodeLines(table))
            {
                n++;
                var rect = p.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                if (rect.IsEmpty || rect.Bottom < 0 || rect.Top > Editor.ActualHeight)
                    continue;
                if (numbers == _lineNumbers.Count)
                {
                    var tb = new TextBlock
                    {
                        FontFamily = CodeFamily, FontSize = CodeFontSize, Foreground = CodeLineNumber,
                        TextAlignment = TextAlignment.Right, Width = CodeGutter - 18,
                    };
                    _lineNumbers.Add(tb);
                    CodeLayer.Children.Add(tb);
                }
                var number = _lineNumbers[numbers++];
                number.Text = n.ToString();
                var point = Editor.TranslatePoint(new Point(left + 4, rect.Top), CodeLayer);
                Canvas.SetLeft(number, point.X);
                Canvas.SetTop(number, point.Y);
                number.Visibility = Visibility.Visible;
            }
        }

        for (var i = numbers; i < _lineNumbers.Count; i++)
            _lineNumbers[i].Visibility = Visibility.Collapsed;
        for (var i = buttons; i < _codeButtons.Count; i++)
        {
            _codeButtons[i].Copy.Visibility = Visibility.Collapsed;
            _codeButtons[i].Remove.Visibility = Visibility.Collapsed;
        }
    }

    private void PlaceAt(Button button, double x, double y, double width)
    {
        var point = Editor.TranslatePoint(new Point(x, y), TableLayer);
        button.Width = width;
        Canvas.SetLeft(button, point.X);
        Canvas.SetTop(button, point.Y);
        button.Visibility = Visibility.Visible;
    }

    private Button MakeCodeButton(string content, string tooltip, bool copy)
    {
        var button = new Button
        {
            Content = content, ToolTip = tooltip, Height = 24,
            Style = (Style)FindResource("CodeHeaderButton"),
            FontFamily = copy ? (FontFamily)FindResource("UiFont") : new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
        };
        button.Click += (_, _) =>
        {
            if (button.Tag is not Table table) return;
            if (copy) CopyCode(table);
            else if (!Editor.IsReadOnly) RemoveTable(table);
        };
        TableLayer.Children.Add(button);
        return button;
    }

    private void CopyCode(Table table)
    {
        var text = string.Join(Environment.NewLine, CodeLines(table).Select(LineText));
        try
        {
            Clipboard.SetText(text);
            ShowStatus("Código copiado.");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            ShowStatus("Não foi possível copiar (área de transferência ocupada).");
        }
    }

    // ================================================================ Exportação

    /// <summary>
    /// Fontes embutidas aparecem no RTF/XAML com o caminho interno ("./Assets/fonts/#JetBrains Mono");
    /// troca pelo nome da fonte, que é o que outros programas entendem.
    /// </summary>
    private static string PlainFontNames(string text) =>
        Regex.Replace(text, @"(?:pack://application:,,,)?\.?/?Assets/fonts/#", "");
}
