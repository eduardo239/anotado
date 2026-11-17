using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Anotado;

public partial class MainWindow : Window
{
    private const string AppName = "Anotado";
    private const string DefaultFont = "Calibri";
    private const double DefaultFontSizePt = 11;
    private const string FileFilter =
        "Documento RTF (*.rtf)|*.rtf|Texto sem formatação (*.txt)|*.txt";

    private static readonly double[] FontSizesPt = [8, 9, 10, 11, 12, 14, 16, 18, 20, 22, 24, 26, 28, 36, 48, 72];

    private static readonly (string Name, Color Color)[] TextColors =
    [
        ("Automático (preto)", Colors.Black),
        ("Cinza", Color.FromRgb(0x59, 0x59, 0x59)),
        ("Vermelho", Color.FromRgb(0xC0, 0x00, 0x00)),
        ("Laranja", Color.FromRgb(0xE3, 0x6C, 0x09)),
        ("Verde", Color.FromRgb(0x00, 0x80, 0x3C)),
        ("Azul", Color.FromRgb(0x1F, 0x4E, 0xA8)),
        ("Azul-claro", Color.FromRgb(0x00, 0x70, 0xC0)),
        ("Roxo", Color.FromRgb(0x70, 0x30, 0xA0)),
    ];

    private static readonly (string Name, Color Color)[] HighlightColors =
    [
        ("Amarelo", Color.FromRgb(0xFF, 0xF1, 0x76)),
        ("Verde", Color.FromRgb(0xB9, 0xF6, 0xCA)),
        ("Azul", Color.FromRgb(0xB3, 0xE5, 0xFC)),
        ("Rosa", Color.FromRgb(0xF8, 0xBB, 0xD0)),
        ("Cinza", Color.FromRgb(0xE0, 0xE0, 0xE0)),
    ];

    private string? _filePath;
    private bool _dirty;
    private bool _loading;
    private bool _syncingToolbar;

    public MainWindow()
    {
        InitializeComponent();

        FontFamilyBox.ItemsSource = Fonts.SystemFontFamilies.OrderBy(f => f.Source).ToList();
        FontSizeBox.ItemsSource = FontSizesPt;

        // Atualiza a barra de ferramentas depois de qualquer comando aplicado ao editor
        // (inclusive atalhos como Ctrl+B, que não mudam a seleção).
        Editor.AddHandler(CommandManager.ExecutedEvent,
            new ExecutedRoutedEventHandler((_, _) => ScheduleToolbarSync()), handledEventsToo: true);

        NewDocument();

        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1]))
            LoadFile(args[1]);

        Loaded += (_, _) => Editor.Focus();
    }

    // ---------------------------------------------------------------- Documento

    private string DocumentName =>
        _filePath is null ? "Documento sem título" : System.IO.Path.GetFileName(_filePath);

    private TextRange FullRange => new(Editor.Document.ContentStart, Editor.Document.ContentEnd);

    private static FlowDocument CreateBaseDocument() => new()
    {
        FontFamily = new FontFamily(DefaultFont),
        FontSize = PtToDip(DefaultFontSizePt),
        PagePadding = new Thickness(72),
        Language = System.Windows.Markup.XmlLanguage.GetLanguage("pt-BR"),
    };

    private void SetDocument(FlowDocument doc, string? path)
    {
        _loading = true;
        Editor.Document = doc;
        Editor.CaretPosition = doc.ContentStart;
        _loading = false;

        _filePath = path;
        _dirty = false;
        UpdateTitle();
        UpdateCounts();
        ScheduleToolbarSync();
    }

    private void NewDocument()
    {
        var doc = CreateBaseDocument();
        doc.Blocks.Add(new Paragraph());
        SetDocument(doc, null);
    }

    private void LoadFile(string path)
    {
        try
        {
            var doc = CreateBaseDocument();
            var range = new TextRange(doc.ContentStart, doc.ContentEnd);

            if (IsTextFile(path))
            {
                range.Text = File.ReadAllText(path); // detecta UTF-8/UTF-16 pelo BOM
            }
            else
            {
                using var stream = File.OpenRead(path);
                range.Load(stream, DataFormats.Rtf);
            }

            SetDocument(doc, path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Não foi possível abrir o arquivo.\n\n{ex.Message}",
                AppName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool SaveToFile(string path)
    {
        try
        {
            if (IsTextFile(path))
            {
                File.WriteAllText(path, FullRange.Text, new UTF8Encoding(true));
            }
            else
            {
                using var stream = File.Create(path);
                FullRange.Save(stream, DataFormats.Rtf);
            }

            _filePath = path;
            _dirty = false;
            UpdateTitle();
            ShowStatus($"Salvo às {DateTime.Now:HH:mm}");
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Não foi possível salvar o arquivo.\n\n{ex.Message}",
                AppName, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private bool Save() => _filePath is null ? SaveAs() : SaveToFile(_filePath);

    private bool SaveAs()
    {
        var dialog = new SaveFileDialog
        {
            Filter = FileFilter,
            DefaultExt = ".rtf",
            FileName = _filePath is null ? "Documento" : System.IO.Path.GetFileNameWithoutExtension(_filePath),
        };
        if (_filePath is not null && IsTextFile(_filePath))
            dialog.FilterIndex = 2;

        if (dialog.ShowDialog(this) != true)
            return false;

        if (IsTextFile(dialog.FileName) && HasRichContent())
        {
            var answer = MessageBox.Show(this,
                "Salvar como texto sem formatação vai descartar negrito, cores, imagens e outros estilos.\n\nDeseja continuar?",
                AppName, MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
                return false;
        }

        return SaveToFile(dialog.FileName);
    }

    /// <summary>Retorna true se pode prosseguir (salvou ou descartou), false se o usuário cancelou.</summary>
    private bool ConfirmDiscardChanges()
    {
        if (!_dirty)
            return true;

        var answer = MessageBox.Show(this, $"Deseja salvar as alterações em \"{DocumentName}\"?",
            AppName, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        return answer switch
        {
            MessageBoxResult.Yes => Save(),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    private bool HasRichContent()
    {
        // Heurística simples: há algo além de parágrafos com texto puro?
        foreach (var block in Editor.Document.Blocks)
        {
            if (block is not Paragraph p) return true;
            foreach (var inline in p.Inlines)
            {
                if (inline is not Run run) return true;
                if (run.FontWeight != FontWeights.Normal || run.FontStyle != FontStyles.Normal ||
                    run.TextDecorations.Count > 0 || run.Background is not null)
                    return true;
            }
        }
        return false;
    }

    private static bool IsTextFile(string path) =>
        string.Equals(System.IO.Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase);

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!ConfirmDiscardChanges())
            e.Cancel = true;
        base.OnClosing(e);
    }

    // ---------------------------------------------------------------- Comandos de arquivo

    private void New_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (ConfirmDiscardChanges())
            NewDocument();
    }

    private void Open_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (!ConfirmDiscardChanges())
            return;

        var dialog = new OpenFileDialog
        {
            Filter = "Documentos suportados (*.rtf;*.txt)|*.rtf;*.txt|" + FileFilter + "|Todos os arquivos (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == true)
            LoadFile(dialog.FileName);
    }

    private void Save_Executed(object sender, ExecutedRoutedEventArgs e) => Save();

    private void SaveAs_Executed(object sender, ExecutedRoutedEventArgs e) => SaveAs();

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Print_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true)
            return;

        // Imprime uma cópia para não alterar o layout do documento em edição.
        var copy = CreateBaseDocument();
        using (var buffer = new MemoryStream())
        {
            FullRange.Save(buffer, DataFormats.XamlPackage);
            buffer.Position = 0;
            new TextRange(copy.ContentStart, copy.ContentEnd).Load(buffer, DataFormats.XamlPackage);
        }

        copy.PageWidth = dialog.PrintableAreaWidth;
        copy.PageHeight = dialog.PrintableAreaHeight;
        copy.PagePadding = new Thickness(72);
        copy.ColumnGap = 0;
        copy.ColumnWidth = dialog.PrintableAreaWidth;

        dialog.PrintDocument(((IDocumentPaginatorSource)copy).DocumentPaginator, DocumentName);
    }

    // ---------------------------------------------------------------- Estado do editor

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading)
            return;

        if (!_dirty)
        {
            _dirty = true;
            UpdateTitle();
        }
        UpdateCounts();
    }

    private void Editor_SelectionChanged(object sender, RoutedEventArgs e) => ScheduleToolbarSync();

    private void UpdateTitle() =>
        Title = $"{(_dirty ? "• " : "")}{DocumentName} - {AppName}";

    private void UpdateCounts()
    {
        var text = FullRange.Text;
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var chars = text.Count(c => c != '\r' && c != '\n');
        CountText.Text = $"{words:N0} {(words == 1 ? "palavra" : "palavras")}  ·  {chars:N0} caracteres";
    }

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (StatusText.Text == message) StatusText.Text = "";
        };
        timer.Start();
    }

    private void ScheduleToolbarSync() =>
        Dispatcher.InvokeAsync(SyncToolbar, DispatcherPriority.Background);

    private void SyncToolbar()
    {
        _syncingToolbar = true;
        try
        {
            var sel = Editor.Selection;

            BoldButton.IsChecked = sel.GetPropertyValue(TextElement.FontWeightProperty) is FontWeight w
                                   && w.ToOpenTypeWeight() >= FontWeights.SemiBold.ToOpenTypeWeight();
            ItalicButton.IsChecked = sel.GetPropertyValue(TextElement.FontStyleProperty) is FontStyle s
                                     && s == FontStyles.Italic;
            UnderlineButton.IsChecked = sel.GetPropertyValue(Inline.TextDecorationsProperty) is TextDecorationCollection d
                                        && d.Any(x => x.Location == TextDecorationLocation.Underline);

            var align = sel.GetPropertyValue(Paragraph.TextAlignmentProperty) as TextAlignment?;
            AlignLeftButton.IsChecked = align == TextAlignment.Left;
            AlignCenterButton.IsChecked = align == TextAlignment.Center;
            AlignRightButton.IsChecked = align == TextAlignment.Right;
            AlignJustifyButton.IsChecked = align == TextAlignment.Justify;

            if (!FontFamilyBox.IsKeyboardFocusWithin)
                FontFamilyBox.Text = sel.GetPropertyValue(TextElement.FontFamilyProperty) is FontFamily ff ? ff.Source : "";

            if (!FontSizeBox.IsKeyboardFocusWithin)
                FontSizeBox.Text = sel.GetPropertyValue(TextElement.FontSizeProperty) is double size
                    ? Math.Round(DipToPt(size), 1).ToString(CultureInfo.CurrentCulture)
                    : "";
        }
        finally
        {
            _syncingToolbar = false;
        }
    }

    // ---------------------------------------------------------------- Fonte

    private void FontFamilyBox_DropDownClosed(object? sender, EventArgs e)
    {
        if (FontFamilyBox.SelectedItem is FontFamily family)
            ApplyFontFamily(family);
    }

    private void FontFamilyBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var name = FontFamilyBox.Text.Trim();
        if (name.Length > 0)
            ApplyFontFamily(new FontFamily(name));
        e.Handled = true;
    }

    private void ApplyFontFamily(FontFamily family)
    {
        if (_syncingToolbar) return;
        Editor.Selection.ApplyPropertyValue(TextElement.FontFamilyProperty, family);
        Editor.Focus();
    }

    private void FontSizeBox_DropDownClosed(object? sender, EventArgs e)
    {
        if (FontSizeBox.SelectedItem is double pt)
            ApplyFontSize(pt);
    }

    private void FontSizeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var text = FontSizeBox.Text.Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var pt) ||
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out pt))
        {
            ApplyFontSize(pt);
        }
        e.Handled = true;
    }

    private void ApplyFontSize(double pt)
    {
        if (_syncingToolbar) return;
        pt = Math.Clamp(pt, 1, 1638);
        Editor.Selection.ApplyPropertyValue(TextElement.FontSizeProperty, PtToDip(pt));
        Editor.Focus();
    }

    private void FontColor_Click(object sender, RoutedEventArgs e) =>
        ShowColorMenu(FontColorButton, TextElement.ForegroundProperty, TextColors, noneLabel: null);

    private void Highlight_Click(object sender, RoutedEventArgs e) =>
        ShowColorMenu(HighlightButton, TextElement.BackgroundProperty, HighlightColors, noneLabel: "Sem realce");

    private void ShowColorMenu(Button owner, DependencyProperty property,
        (string Name, Color Color)[] colors, string? noneLabel)
    {
        var menu = new ContextMenu { PlacementTarget = owner, Placement = PlacementMode.Bottom };

        foreach (var (name, color) in colors)
        {
            var item = new MenuItem
            {
                Header = name,
                Icon = new Border
                {
                    Width = 16, Height = 16,
                    Background = new SolidColorBrush(color),
                    BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
                },
            };
            item.Click += (_, _) =>
            {
                Editor.Selection.ApplyPropertyValue(property, new SolidColorBrush(color));
                Editor.Focus();
            };
            menu.Items.Add(item);
        }

        if (noneLabel is not null)
        {
            menu.Items.Add(new Separator());
            var none = new MenuItem { Header = noneLabel };
            none.Click += (_, _) =>
            {
                Editor.Selection.ApplyPropertyValue(property, null);
                Editor.Focus();
            };
            menu.Items.Add(none);
        }

        menu.IsOpen = true;
    }

    // ---------------------------------------------------------------- Inserir

    private void InsertImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Imagens (*.png;*.jpg;*.jpeg;*.gif;*.bmp)|*.png;*.jpg;*.jpeg;*.gif;*.bmp",
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(dialog.FileName);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();

            var maxWidth = Editor.Width - 2 * 72 - 8;
            var image = new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                Width = Math.Min(bitmap.PixelWidth, maxWidth),
            };

            Editor.Selection.Text = "";
            var position = Editor.CaretPosition.GetInsertionPosition(LogicalDirection.Forward);
            _ = new InlineUIContainer(image, position);
            Editor.Focus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Não foi possível inserir a imagem.\n\n{ex.Message}",
                AppName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void InsertDate_Click(object sender, RoutedEventArgs e)
    {
        Editor.Selection.Text = DateTime.Now.ToString("g", CultureInfo.CurrentCulture);
        Editor.CaretPosition = Editor.Selection.End;
        Editor.Focus();
    }

    // ---------------------------------------------------------------- Localizar e substituir

    private void Find_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        FindBar.Visibility = Visibility.Visible;
        if (!Editor.Selection.IsEmpty && !Editor.Selection.Text.Contains('\n'))
            FindBox.Text = Editor.Selection.Text;
        FindBox.Focus();
        FindBox.SelectAll();
    }

    private void FindBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (sender == ReplaceBox) ReplaceCurrent(); else FindNext();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseFindBar();
            e.Handled = true;
        }
    }

    private void FindNext_Click(object sender, RoutedEventArgs e) => FindNext();
    private void Replace_Click(object sender, RoutedEventArgs e) => ReplaceCurrent();
    private void CloseFind_Click(object sender, RoutedEventArgs e) => CloseFindBar();

    private void CloseFindBar()
    {
        FindBar.Visibility = Visibility.Collapsed;
        Editor.Focus();
    }

    private StringComparison Comparison =>
        MatchCaseBox.IsChecked == true ? StringComparison.CurrentCulture : StringComparison.CurrentCultureIgnoreCase;

    private bool FindNext()
    {
        var term = FindBox.Text;
        if (term.Length == 0)
            return false;

        var found = FindText(Editor.Selection.End, term)
                    ?? FindText(Editor.Document.ContentStart, term); // recomeça do início

        if (found is null)
        {
            ShowStatus($"\"{term}\" não foi encontrado.");
            return false;
        }

        Editor.Selection.Select(found.Start, found.End);
        ScrollIntoView(found.Start);
        return true;
    }

    private void ReplaceCurrent()
    {
        var term = FindBox.Text;
        if (term.Length == 0)
            return;

        if (string.Equals(Editor.Selection.Text, term, Comparison))
        {
            Editor.Selection.Text = ReplaceBox.Text;
            Editor.Selection.Select(Editor.Selection.End, Editor.Selection.End);
        }
        FindNext();
    }

    private void ReplaceAll_Click(object sender, RoutedEventArgs e)
    {
        var term = FindBox.Text;
        if (term.Length == 0)
            return;

        var count = 0;
        Editor.BeginChange(); // um único "desfazer" para todas as substituições
        try
        {
            var position = Editor.Document.ContentStart;
            while (FindText(position, term) is { } match)
            {
                match.Text = ReplaceBox.Text;
                position = match.End;
                count++;
            }
        }
        finally
        {
            Editor.EndChange();
        }

        ShowStatus(count == 0 ? $"\"{term}\" não foi encontrado." : $"{count} substituição(ões) feita(s).");
    }

    /// <summary>Procura o termo a partir de <paramref name="start"/>, dentro de cada trecho de texto.</summary>
    private TextRange? FindText(TextPointer start, string term)
    {
        var comparison = Comparison;
        for (var pointer = start; pointer is not null; pointer = pointer.GetNextContextPosition(LogicalDirection.Forward))
        {
            if (pointer.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.Text)
                continue;

            var run = pointer.GetTextInRun(LogicalDirection.Forward);
            var index = run.IndexOf(term, comparison);
            if (index < 0)
                continue;

            var matchStart = pointer.GetPositionAtOffset(index);
            var matchEnd = matchStart?.GetPositionAtOffset(term.Length);
            if (matchStart is not null && matchEnd is not null)
                return new TextRange(matchStart, matchEnd);
        }
        return null;
    }

    private void ScrollIntoView(TextPointer position)
    {
        var rect = position.GetCharacterRect(LogicalDirection.Forward);
        if (rect.IsEmpty) return;
        if (rect.Top < 0 || rect.Bottom > Editor.ViewportHeight)
            Editor.ScrollToVerticalOffset(Editor.VerticalOffset + rect.Top - Editor.ViewportHeight / 3);
    }

    // ---------------------------------------------------------------- Menu de contexto (ortografia)

    private void Editor_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = Editor.ContextMenu!;
        menu.Items.Clear();

        var position = Editor.GetPositionFromPoint(Mouse.GetPosition(Editor), snapToText: true);
        var error = position is null ? null : Editor.GetSpellingError(position);

        if (error is not null)
        {
            var suggestions = error.Suggestions.Take(6).ToList();
            foreach (var suggestion in suggestions)
            {
                var item = new MenuItem { Header = suggestion, FontWeight = FontWeights.SemiBold };
                item.Click += (_, _) => error.Correct(suggestion);
                menu.Items.Add(item);
            }
            if (suggestions.Count == 0)
                menu.Items.Add(new MenuItem { Header = "(sem sugestões)", IsEnabled = false });

            var ignore = new MenuItem { Header = "Ignorar tudo" };
            ignore.Click += (_, _) => error.IgnoreAll();
            menu.Items.Add(ignore);
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(new MenuItem { Header = "Recortar", Command = ApplicationCommands.Cut, CommandTarget = Editor });
        menu.Items.Add(new MenuItem { Header = "Copiar", Command = ApplicationCommands.Copy, CommandTarget = Editor });
        menu.Items.Add(new MenuItem { Header = "Colar", Command = ApplicationCommands.Paste, CommandTarget = Editor });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Selecionar tudo", Command = ApplicationCommands.SelectAll, CommandTarget = Editor });
    }

    // ---------------------------------------------------------------- Zoom

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ZoomTransform is null) return; // ainda inicializando
        var scale = e.NewValue / 100.0;
        ZoomTransform.ScaleX = scale;
        ZoomTransform.ScaleY = scale;
        ZoomText.Text = $"{e.NewValue:0}%";
    }

    private void PageArea_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        ZoomSlider.Value += e.Delta > 0 ? 10 : -10;
        e.Handled = true;
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomSlider.Value += 10;
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomSlider.Value -= 10;
    private void ZoomReset_Click(object sender, RoutedEventArgs e) => ZoomSlider.Value = 100;

    // ---------------------------------------------------------------- Ajuda

    private void About_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this, "Anotado 1.0\nEditor de texto simples para Windows.",
            "Sobre o Anotado", MessageBoxButton.OK, MessageBoxImage.Information);

    // ---------------------------------------------------------------- Utilitários

    private static double PtToDip(double pt) => pt * 96.0 / 72.0;
    private static double DipToPt(double dip) => dip * 72.0 / 96.0;
}
