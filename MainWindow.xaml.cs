using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Printing;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Anotado;

/// <summary>Estilos de parágrafo oferecidos na aba Início.</summary>
public enum ParagraphKind { Normal, Title, Heading1, Heading2, Quote }

/// <summary>Item da estrutura do documento (painel de navegação).</summary>
public sealed record OutlineEntry(string Number, string Text, int Level, Paragraph Paragraph)
{
    public Thickness Indent => new(Level > 1 ? 14 : 0, 0, 0, 0);
}

public partial class MainWindow : Window
{
    private const string AppName = "Anotado";
    private const string DefaultFont = "Calibri";
    private const double BodySizePt = 12;
    private const string FileFilter =
        "Documento RTF (*.rtf)|*.rtf|Texto sem formatação (*.txt)|*.txt";

    private static readonly double[] FontSizesPt = [8, 9, 10, 11, 12, 14, 16, 18, 20, 22, 24, 26, 28, 36, 48, 72];
    private static readonly string[] Months = ["JAN", "FEV", "MAR", "ABR", "MAI", "JUN", "JUL", "AGO", "SET", "OUT", "NOV", "DEZ"];

    // Cores fixas usadas *dentro* do documento (não mudam com o tema, pois vão para o arquivo).
    private static readonly Brush QuoteBackground = Frozen(Color.FromRgb(0xE5, 0xE9, 0xDC));
    private static readonly Brush QuoteBar = Frozen(Color.FromRgb(0x8C, 0x9C, 0x74));
    private static readonly Brush TableLine = Frozen(Color.FromRgb(0xBD, 0xBD, 0xB5));

    private static readonly (string Name, Color Color)[] TextColors =
    [
        ("Automático", Colors.Black), // remove a cor: segue a tinta da página (preto ou claro)
        ("Cinza", Color.FromRgb(0x59, 0x59, 0x59)),
        ("Vermelho", Color.FromRgb(0xC0, 0x00, 0x00)),
        ("Laranja", Color.FromRgb(0xE3, 0x6C, 0x09)),
        ("Verde", Color.FromRgb(0x3B, 0x6E, 0x12)),
        ("Azul", Color.FromRgb(0x1F, 0x4E, 0xA8)),
        ("Roxo", Color.FromRgb(0x70, 0x30, 0xA0)),
    ];

    private static readonly (string Name, Color Color)[] HighlightColors =
    [
        ("Verde-limão", Color.FromRgb(0xDC, 0xF5, 0xA8)),
        ("Amarelo", Color.FromRgb(0xFF, 0xF1, 0x76)),
        ("Azul", Color.FromRgb(0xB3, 0xE5, 0xFC)),
        ("Rosa", Color.FromRgb(0xF8, 0xBB, 0xD0)),
        ("Cinza", Color.FromRgb(0xE0, 0xE0, 0xE0)),
    ];

    private static AppSettings Settings => AppSettings.Current;

    private readonly DispatcherTimer _refreshTimer;
    private string? _filePath;
    private bool _dirty;
    private bool _loading;
    private bool _initializing = true;
    private bool _syncingToolbar;
    private bool _focusMode;
    private List<OutlineEntry> _outline = [];

    // Autocompletar: sugestão em cinza após o cursor, aceita com Tab.
    private readonly WordCompleter _completer = new();
    private string? _suggestion;
    private bool _suggestionDismissed;

    public MainWindow()
    {
        InitializeComponent();

        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(Width, workArea.Width * 0.95);
        Height = Math.Min(Height, workArea.Height * 0.95);

        FontFamilyBox.ItemsSource = Fonts.SystemFontFamilies.OrderBy(f => f.Source).ToList();
        FontSizeBox.ItemsSource = FontSizesPt;

        // Atualiza a barra depois de qualquer comando no editor (inclusive atalhos como Ctrl+B).
        Editor.AddHandler(CommandManager.ExecutedEvent,
            new ExecutedRoutedEventHandler((_, _) => ScheduleToolbarSync()), handledEventsToo: true);
        Editor.AddHandler(Hyperlink.RequestNavigateEvent, new RequestNavigateEventHandler(Hyperlink_RequestNavigate));

        // Página escura: o que sai do editor (copiar/arrastar) leva a tinta escura padrão,
        // e o que entra (colar/soltar) tem o preto convertido em cor automática.
        DataObject.AddCopyingHandler(Editor, Editor_Copying);
        Editor.AddHandler(CommandManager.ExecutedEvent, new ExecutedRoutedEventHandler((_, e) =>
        {
            if (e.Command == ApplicationCommands.Paste) NormalizeInk(Editor.Document);
        }), handledEventsToo: true);
        Editor.AddHandler(DragDrop.DropEvent, new DragEventHandler((_, _) =>
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => NormalizeInk(Editor.Document))), handledEventsToo: true);

        // Estatísticas e estrutura são recalculadas pouco depois de parar de digitar.
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _refreshTimer.Tick += (_, _) => { _refreshTimer.Stop(); RefreshStatsAndOutline(); };

        PageFormatBox.SelectedIndex = (int)Settings.PageFormat;
        OrientationBox.SelectedIndex = Settings.Landscape ? 1 : 0;
        MarginsBox.SelectedIndex = (int)Settings.Margins;
        GoalBox.Text = Settings.WordGoal.ToString(CultureInfo.CurrentCulture);
        BackupToggle.IsChecked = Settings.BackupEnabled;
        BackupKeepBox.SelectedIndex = Math.Max(0, Array.IndexOf(BackupKeepOptions, Settings.BackupKeep));
        UpdateBackupUi();
        ApplyPanelsVisibility();

        NewDocument();

        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1]))
            LoadFile(args[1]);

        UpdateThemeButtons();
        ThemeManager.ThemeChanged += UpdateThemeButtons;
        Closed += (_, _) => ThemeManager.ThemeChanged -= UpdateThemeButtons;

        _initializing = false;
        AutoCompleteToggle.IsChecked = Settings.AutoComplete;
        Editor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => ScheduleSuggestionLayout()));
        Editor.SizeChanged += (_, _) => ScheduleSuggestionLayout();
        Editor.LostKeyboardFocus += (_, _) => HideSuggestion();

        Loaded += (_, _) => Editor.Focus();
    }

    // ================================================================ Documento

    private string DocumentName =>
        _filePath is null ? "Documento sem título" : System.IO.Path.GetFileName(_filePath);

    private string DisplayTitle =>
        _filePath is null ? "Documento sem título" : System.IO.Path.GetFileNameWithoutExtension(_filePath);

    private TextRange FullRange => new(Editor.Document.ContentStart, Editor.Document.ContentEnd);

    private static FlowDocument CreateBaseDocument() => new()
    {
        FontFamily = new FontFamily(DefaultFont),
        FontSize = PtToDip(BodySizePt),
        TextAlignment = TextAlignment.Left, // o padrão do WPF é justificado
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
        ApplyPageSetup();
        UpdateDocumentInfo();
        RefreshStatsAndOutline();
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

            NormalizeInk(doc);
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
                WithPrintInk(() => FullRange.Save(stream, DataFormats.Rtf));
            }

            _filePath = path;
            _dirty = false;
            UpdateDocumentInfo();
            ShowStatus($"Salvo às {DateTime.Now:HH:mm}");

            if (Settings.BackupEnabled)
                _ = RunBackupAsync(path); // em segundo plano: não trava a digitação
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
        foreach (var block in Editor.Document.Blocks)
        {
            if (block is not Paragraph p) return true;
            if (DetectKind(p) != ParagraphKind.Normal) return true;
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

    // ================================================================ Arquivo

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

    private void FileTab_Click(object sender, RoutedEventArgs e)
    {
        FileTab.IsChecked = true;

        var menu = new ContextMenu { PlacementTarget = FileTab, Placement = PlacementMode.Bottom };
        void Add(string header, ICommand command, string? gesture = null) =>
            menu.Items.Add(new MenuItem { Header = header, Command = command, CommandTarget = Editor, InputGestureText = gesture ?? "" });

        Add("Novo", ApplicationCommands.New, "Ctrl+N");
        Add("Abrir...", ApplicationCommands.Open, "Ctrl+O");
        Add("Salvar", ApplicationCommands.Save, "Ctrl+S");
        Add("Salvar como...", ApplicationCommands.SaveAs, "Ctrl+Shift+S");
        menu.Items.Add(new Separator());
        Add("Imprimir...", ApplicationCommands.Print, "Ctrl+P");
        menu.Items.Add(new Separator());
        var exit = new MenuItem { Header = "Sair" };
        exit.Click += (_, _) => Close();
        menu.Items.Add(exit);

        menu.Closed += (_, _) => FileTab.IsChecked = false;
        menu.IsOpen = true;
    }

    private void Print_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        var dialog = new PrintDialog();
        try
        {
            dialog.PrintTicket.PageOrientation = Settings.Landscape ? PageOrientation.Landscape : PageOrientation.Portrait;
            dialog.PrintTicket.PageMediaSize = new PageMediaSize(Settings.PageFormat switch
            {
                PageFormat.Carta => PageMediaSizeName.NorthAmericaLetter,
                PageFormat.Oficio => PageMediaSizeName.NorthAmericaLegal,
                _ => PageMediaSizeName.ISOA4,
            });
        }
        catch (Exception) { /* impressora sem suporte: usa o padrão dela */ }

        if (dialog.ShowDialog() != true)
            return;

        // Imprime uma cópia para não alterar o layout do documento em edição.
        var copy = CreateBaseDocument();
        copy.Resources.Add(typeof(Paragraph), Editor.Resources[typeof(Paragraph)]);
        using (var buffer = new MemoryStream())
        {
            WithPrintInk(() => FullRange.Save(buffer, DataFormats.XamlPackage));
            buffer.Position = 0;
            new TextRange(copy.ContentStart, copy.ContentEnd).Load(buffer, DataFormats.XamlPackage);
        }

        copy.PageWidth = dialog.PrintableAreaWidth;
        copy.PageHeight = dialog.PrintableAreaHeight;
        copy.PagePadding = new Thickness(PageSetup.Margin(Settings.Margins));
        copy.ColumnGap = 0;
        copy.ColumnWidth = dialog.PrintableAreaWidth;

        dialog.PrintDocument(((IDocumentPaginatorSource)copy).DocumentPaginator, DocumentName);
    }

    // ================================================================ Estado e informações

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading)
            return;

        if (!_dirty)
        {
            _dirty = true;
            UpdateDocumentInfo();
        }
        _refreshTimer.Stop();
        _refreshTimer.Start();

        _suggestionDismissed = false;
        UpdateSuggestion();
    }

    private void Editor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        ScheduleToolbarSync();
        HighlightCurrentSection();
        UpdateSuggestion();
    }

    // ================================================================ Autocompletar

    /// <summary>Letras imediatamente antes do cursor (a palavra sendo digitada), ou null se não houver.</summary>
    private string? WordBeforeCaret()
    {
        var before = Editor.CaretPosition.GetTextInRun(LogicalDirection.Backward);
        var start = before.Length;
        while (start > 0 && WordCompleter.IsWordChar(before[start - 1])) start--;
        return start == before.Length ? null : before[start..];
    }

    private void UpdateSuggestion()
    {
        string? suggestion = null;
        if (Settings.AutoComplete && !_suggestionDismissed && !_loading && !Editor.IsReadOnly
            && Editor.IsKeyboardFocusWithin && Editor.Selection.IsEmpty)
        {
            // Só sugere no fim da palavra (nada de letra logo depois do cursor).
            var after = Editor.CaretPosition.GetTextInRun(LogicalDirection.Forward);
            if (after.Length == 0 || !WordCompleter.IsWordChar(after[0]))
            {
                var prefix = WordBeforeCaret();
                if (prefix is not null)
                    suggestion = _completer.Suggest(prefix);
            }
        }

        _suggestion = suggestion;
        ScheduleSuggestionLayout();
    }

    private void HideSuggestion()
    {
        _suggestion = null;
        SuggestionText.Visibility = Visibility.Collapsed;
    }

    // O layout do texto recém-digitado só fica pronto depois desta passada; posiciona em seguida.
    private void ScheduleSuggestionLayout() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, LayoutSuggestion);

    private void LayoutSuggestion()
    {
        if (_suggestion is null)
        {
            SuggestionText.Visibility = Visibility.Collapsed;
            return;
        }

        var caret = Editor.CaretPosition;
        var rect = caret.GetCharacterRect(LogicalDirection.Backward);
        if (rect.IsEmpty)
        {
            SuggestionText.Visibility = Visibility.Collapsed;
            return;
        }

        // Mesma formatação do trecho em que o cursor está.
        if (caret.Parent is TextElement element)
        {
            SuggestionText.FontFamily = element.FontFamily;
            SuggestionText.FontSize = element.FontSize;
            SuggestionText.FontStyle = element.FontStyle;
            SuggestionText.FontWeight = element.FontWeight;
        }
        SuggestionText.Text = _suggestion;
        SuggestionText.Visibility = Visibility.Visible;
        SuggestionText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        // Alinha pela parte de baixo da linha, para coincidir com a linha de base do texto.
        var origin = Editor.TranslatePoint(new Point(rect.Right, rect.Bottom), SuggestionLayer);
        Canvas.SetLeft(SuggestionText, origin.X);
        Canvas.SetTop(SuggestionText, origin.Y - SuggestionText.DesiredSize.Height);
    }

    private void AcceptSuggestion()
    {
        var suffix = _suggestion!;
        HideSuggestion();
        // Ponteiro com gravidade para frente: acaba depois do texto inserido.
        var end = Editor.CaretPosition.GetPositionAtOffset(0, LogicalDirection.Forward)!;
        Editor.Selection.Text = suffix;
        Editor.CaretPosition = end;
    }

    private void AutoCompleteToggle_Click(object sender, RoutedEventArgs e)
    {
        Settings.AutoComplete = AutoCompleteToggle.IsChecked == true;
        Settings.Save();
        UpdateSuggestion();
    }

    private void UpdateDocumentInfo()
    {
        Title = $"{(_dirty ? "• " : "")}{DocumentName} - {AppName}";
        DocTitleText.Text = DisplayTitle;

        var folder = _filePath is null ? null : new DirectoryInfo(System.IO.Path.GetDirectoryName(_filePath)!);
        DocPathText.Text = folder is null
            ? "Ainda não salvo"
            : folder.Parent is { } parent ? $"{parent.Name.TrimEnd('\\')} / {folder.Name}" : folder.Name.TrimEnd('\\');

        var savedText = _dirty ? "Alterações não salvas" : _filePath is null ? "Novo documento" : "Salvo";
        var dotKey = _dirty ? "WarningBrush" : "AccentBrush";
        SavedText.Text = StatusSavedText.Text = savedText;
        SavedDot.SetResourceReference(Shape.FillProperty, dotKey);
        StatusDot.SetResourceReference(Shape.FillProperty, dotKey);

        var culture = CultureInfo.GetCultureInfo("pt-BR");
        HeaderPathText.Text = $"ANOTADO / {(folder?.Name.TrimEnd('\\') ?? "Rascunho").ToUpper(culture)}";
        var today = DateTime.Today;
        HeaderDateText.Text = $"{today.Day:00} {Months[today.Month - 1]} {today.Year}";
        FooterNameText.Text = DisplayTitle.ToUpper(culture);
    }

    private void RefreshStatsAndOutline()
    {
        var text = FullRange.Text;
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        _completer.Rebuild(text, WordBeforeCaret());
        var chars = text.Count(c => c != '\r' && c != '\n');
        var minutes = words == 0 ? 0 : (int)Math.Ceiling(words / 200.0); // ~200 palavras por minuto

        StatWords.Text = words.ToString("N0");
        StatChars.Text = chars.ToString("N0");
        StatReading.Text = $"{minutes} min";
        StatusWordsText.Text = $"{words:N0} {(words == 1 ? "palavra" : "palavras")}";
        FooterWordsText.Text = $"{words:N0} PALAVRAS";
        GoalCurrentText.Text = words.ToString("N0");
        UpdateGoalBar(words);

        BuildOutline();
    }

    private int CurrentWordCount =>
        FullRange.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private void UpdateGoalBar(int words)
    {
        var ratio = Settings.WordGoal <= 0 ? 0 : Math.Min(1.0, (double)words / Settings.WordGoal);
        GoalFill.Width = GoalTrack.ActualWidth * ratio;
        GoalCaption.Text = ratio >= 1 ? "Meta atingida. Bom trabalho!" : "Uma ideia de cada vez.";
    }

    private void GoalTrack_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateGoalBar(CurrentWordCount);

    private void GoalBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Editor.Focus(); // dispara o LostFocus, que aplica a meta
        e.Handled = true;
    }

    private void GoalBox_Commit(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(GoalBox.Text.Trim().Replace(".", ""), NumberStyles.Integer, CultureInfo.CurrentCulture, out var goal) && goal > 0)
        {
            Settings.WordGoal = Math.Min(goal, 1_000_000);
            Settings.Save();
        }
        GoalBox.Text = Settings.WordGoal.ToString(CultureInfo.CurrentCulture);
        UpdateGoalBar(CurrentWordCount);
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

    // ================================================================ Barra de formatação

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (RibbonPanels is null || sender is not FrameworkElement { Tag: string panelName })
            return; // ainda carregando a janela

        foreach (FrameworkElement panel in RibbonPanels.Children)
            panel.Visibility = panel.Name == panelName ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Editor is null) return; // ainda carregando a janela

        var readOnly = ModeBox.SelectedIndex == 1;
        Editor.IsReadOnly = readOnly;
        HomePanel.IsEnabled = InsertPanel.IsEnabled = LayoutPanel.IsEnabled = !readOnly;
        ShowStatus(readOnly ? "Modo leitura: o texto está protegido contra alterações." : "Modo edição.");
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

            var paragraph = sel.Start.Paragraph;
            StyleBox.SelectedIndex = paragraph is null ? -1 : (int)DetectKind(paragraph);
        }
        finally
        {
            _syncingToolbar = false;
        }
    }

    // ---------------------------------------------------------------- Estilos de parágrafo

    private void StyleBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingToolbar || Editor is null || StyleBox.SelectedIndex < 0)
            return;
        ApplyParagraphKind((ParagraphKind)StyleBox.SelectedIndex);
        Editor.Focus();
    }

    private void ApplyParagraphKind(ParagraphKind kind)
    {
        var (sizePt, weight, margin) = kind switch
        {
            ParagraphKind.Title => (26.0, FontWeights.Bold, new Thickness(0, 4, 0, 8)),
            ParagraphKind.Heading1 => (17.0, FontWeights.SemiBold, new Thickness(0, 22, 0, 10)),
            ParagraphKind.Heading2 => (13.5, FontWeights.SemiBold, new Thickness(0, 14, 0, 6)),
            ParagraphKind.Quote => (BodySizePt, FontWeights.Normal, new Thickness(0, 8, 0, 14)),
            _ => (BodySizePt, FontWeights.Normal, new Thickness(0, 0, 0, 10)),
        };

        Editor.BeginChange();
        try
        {
            foreach (var p in SelectedParagraphs().ToList())
            {
                var content = new TextRange(p.ContentStart, p.ContentEnd);
                content.ApplyPropertyValue(TextElement.FontSizeProperty, PtToDip(sizePt));
                content.ApplyPropertyValue(TextElement.FontWeightProperty, weight);
                p.FontSize = PtToDip(sizePt); // vale também para o texto digitado depois
                p.FontWeight = weight;
                p.Margin = margin;

                if (kind == ParagraphKind.Quote)
                {
                    p.Background = QuoteBackground;
                    p.BorderBrush = QuoteBar;
                    p.BorderThickness = new Thickness(3, 0, 0, 0);
                    p.Padding = new Thickness(18, 12, 18, 12);
                }
                else
                {
                    p.ClearValue(Block.BackgroundProperty);
                    p.ClearValue(Block.BorderBrushProperty);
                    p.ClearValue(Block.BorderThicknessProperty);
                    p.ClearValue(Block.PaddingProperty);
                }
            }
        }
        finally
        {
            Editor.EndChange();
        }

        RefreshStatsAndOutline();
        ScheduleToolbarSync();
    }

    /// <summary>
    /// Descobre o estilo de um parágrafo pela formatação (assim funciona também em arquivos
    /// reabertos, já que o RTF não guarda o nome do estilo).
    /// </summary>
    private static ParagraphKind DetectKind(Paragraph p)
    {
        if (p.BorderThickness.Left >= 2 && p.Background is not null)
            return ParagraphKind.Quote;

        var content = new TextRange(p.ContentStart, p.ContentEnd);
        var sizeValue = content.IsEmpty ? p.FontSize : content.GetPropertyValue(TextElement.FontSizeProperty);
        var weightValue = content.IsEmpty ? p.FontWeight : content.GetPropertyValue(TextElement.FontWeightProperty);
        if (sizeValue is not double sizeDip)
            return ParagraphKind.Normal;

        var size = DipToPt(sizeDip);
        var bold = weightValue is FontWeight w && w.ToOpenTypeWeight() >= FontWeights.SemiBold.ToOpenTypeWeight();

        if (size >= 22) return ParagraphKind.Title;
        if (size >= 15 && bold) return ParagraphKind.Heading1;
        if (size >= 13 && bold) return ParagraphKind.Heading2;
        return ParagraphKind.Normal;
    }

    private IEnumerable<Paragraph> SelectedParagraphs()
    {
        var sel = Editor.Selection;
        return EnumerateParagraphs(Editor.Document.Blocks)
            .Where(p => p.ContentEnd.CompareTo(sel.Start) >= 0 && p.ContentStart.CompareTo(sel.End) <= 0);
    }

    private static IEnumerable<Paragraph> EnumerateParagraphs(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph p:
                    yield return p;
                    break;
                case List list:
                    foreach (var item in list.ListItems)
                        foreach (var p in EnumerateParagraphs(item.Blocks)) yield return p;
                    break;
                case Section section:
                    foreach (var p in EnumerateParagraphs(section.Blocks)) yield return p;
                    break;
                case Table table:
                    foreach (var group in table.RowGroups)
                        foreach (var row in group.Rows)
                            foreach (var cell in row.Cells)
                                foreach (var p in EnumerateParagraphs(cell.Blocks)) yield return p;
                    break;
            }
        }
    }

    private void LineSpacingBox_DropDownClosed(object? sender, EventArgs e)
    {
        if (LineSpacingBox.SelectedItem is not ComboBoxItem { Tag: string tag })
            return;
        var factor = double.Parse(tag, CultureInfo.InvariantCulture);

        Editor.BeginChange();
        foreach (var p in SelectedParagraphs().ToList())
        {
            if (factor <= 1.0)
            {
                p.ClearValue(Block.LineHeightProperty);
                continue;
            }
            var size = new TextRange(p.ContentStart, p.ContentEnd).GetPropertyValue(TextElement.FontSizeProperty) as double?
                       ?? p.FontSize;
            p.LineHeight = size * 1.25 * factor;
        }
        Editor.EndChange();
        Editor.Focus();
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
                    Width = 16, Height = 16, CornerRadius = new CornerRadius(3),
                    Background = new SolidColorBrush(color),
                    BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
                },
            };
            var automatic = property == TextElement.ForegroundProperty && color == Colors.Black;
            item.Click += (_, _) =>
            {
                Editor.Selection.ApplyPropertyValue(property, automatic ? null : new SolidColorBrush(color));
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

    // ================================================================ Inserir

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

            var maxWidth = Page.Width - 2 * PageSetup.Margin(Settings.Margins) - 8;
            var image = new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                Width = Math.Min(bitmap.PixelWidth, maxWidth),
            };

            Editor.Selection.Text = "";
            _ = new InlineUIContainer(image, Editor.CaretPosition.GetInsertionPosition(LogicalDirection.Forward));
            Editor.Focus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Não foi possível inserir a imagem.\n\n{ex.Message}",
                AppName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void InsertTable_Click(object sender, RoutedEventArgs e)
    {
        const int rows = 3, columns = 3;

        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 12) };
        for (var c = 0; c < columns; c++)
            table.Columns.Add(new TableColumn());

        var group = new TableRowGroup();
        for (var r = 0; r < rows; r++)
        {
            var row = new TableRow();
            for (var c = 0; c < columns; c++)
            {
                row.Cells.Add(new TableCell(new Paragraph { Margin = new Thickness(0) })
                {
                    BorderBrush = TableLine,
                    BorderThickness = new Thickness(0.75),
                    Padding = new Thickness(8, 5, 8, 5),
                });
            }
            group.Rows.Add(row);
        }
        table.RowGroups.Add(group);

        var current = Editor.CaretPosition.Paragraph;
        if (current?.SiblingBlocks is { } siblings)
            siblings.InsertAfter(current, table);
        else
            Editor.Document.Blocks.Add(table);

        if (table.NextBlock is null)
            table.SiblingBlocks!.Add(new Paragraph());

        Editor.CaretPosition = group.Rows[0].Cells[0].ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        Editor.Focus();
    }

    private void InsertLink_Click(object sender, RoutedEventArgs e)
    {
        if (Editor.IsReadOnly) return;

        var address = InputDialog.Ask(this, "Inserir link", "Endereço do link:", "https://");
        if (string.IsNullOrWhiteSpace(address) || address == "https://")
            return;
        if (!address.Contains("://") && !address.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            address = "https://" + address;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
        {
            ShowStatus("Endereço inválido.");
            return;
        }

        try
        {
            Editor.BeginChange();
            TextPointer start, end;
            if (Editor.Selection.IsEmpty)
            {
                var run = new Run(address, Editor.CaretPosition.GetInsertionPosition(LogicalDirection.Forward));
                (start, end) = (run.ContentStart, run.ContentEnd);
            }
            else
            {
                (start, end) = (Editor.Selection.Start, Editor.Selection.End);
            }
            _ = new Hyperlink(start, end) { NavigateUri = uri, ToolTip = $"{uri}\nCtrl+clique para abrir" };
            Editor.CaretPosition = end.GetInsertionPosition(LogicalDirection.Forward);
        }
        catch (InvalidOperationException)
        {
            ShowStatus("Selecione um trecho dentro de um único parágrafo para criar o link.");
        }
        finally
        {
            Editor.EndChange();
        }
        Editor.Focus();
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        if (e.Uri.Scheme is "http" or "https" or "mailto")
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void InsertDate_Click(object sender, RoutedEventArgs e)
    {
        Editor.Selection.Text = DateTime.Now.ToString("g", CultureInfo.CurrentCulture);
        Editor.CaretPosition = Editor.Selection.End;
        Editor.Focus();
    }

    // ================================================================ Página

    private void PageSetup_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;

        Settings.PageFormat = (PageFormat)Math.Max(0, PageFormatBox.SelectedIndex);
        Settings.Landscape = OrientationBox.SelectedIndex == 1;
        Settings.Margins = (PageMargins)Math.Max(0, MarginsBox.SelectedIndex);
        Settings.Save();
        ApplyPageSetup();
    }

    private void ApplyPageSetup()
    {
        var (width, _) = PageSetup.Size(Settings.PageFormat, Settings.Landscape);
        var margin = PageSetup.Margin(Settings.Margins);

        Page.Width = width;
        // O RichTextBox ignora o PagePadding do documento; a margem vai no próprio editor.
        Editor.Document.PagePadding = new Thickness(0);
        Editor.Padding = new Thickness(margin, 22, margin, 24);
        PageHeader.Margin = new Thickness(margin, 40, margin, 0);
        PageFooter.Margin = new Thickness(margin, 0, margin, 16);
    }

    private void ShowPageSetup_Click(object sender, RoutedEventArgs e)
    {
        Settings.ShowDocumentPanel = true;
        Settings.Save();
        if (_focusMode) SetFocusMode(false);
        ApplyPanelsVisibility();
        PageFormatBox.Focus();
    }

    // ================================================================ Backup automático

    private static readonly int[] BackupKeepOptions = [10, 30, 100, 0]; // 0 = todas

    private async Task RunBackupAsync(string savedFile)
    {
        var folder = Settings.BackupFolder;
        if (string.IsNullOrEmpty(folder))
        {
            SetBackupStatus("Escolha uma pasta para ativar o backup.", ok: false);
            return;
        }

        SetBackupStatus("Fazendo backup…", ok: true);
        try
        {
            var keep = Settings.BackupKeep;
            await Task.Run(() => BackupService.Backup(savedFile, folder, keep));
            Settings.LastBackup = DateTime.Now;
            Settings.Save();
            UpdateBackupUi();
        }
        catch (Exception ex)
        {
            SetBackupStatus($"O último backup falhou: {ex.Message}", ok: false);
            ShowStatus("Documento salvo, mas o backup falhou. Veja o painel Documento.");
        }
    }

    private async void BackupToggle_Click(object sender, RoutedEventArgs e)
    {
        if (BackupToggle.IsChecked == true && string.IsNullOrEmpty(Settings.BackupFolder))
        {
            // Primeira vez: sugere a pasta do Google Drive para computador, se estiver instalado.
            SetBackupStatus("Procurando o Google Drive…", ok: true);
            var drive = await Task.Run(BackupService.FindGoogleDrive);
            if (drive is not null)
            {
                Settings.BackupFolder = System.IO.Path.Combine(drive, "Anotado Backup");
                ShowStatus("Pasta do Google Drive encontrada.");
            }
            else if (!ChooseBackupFolder("Google Drive não encontrado. Escolha a pasta de backup"))
            {
                BackupToggle.IsChecked = false;
            }
        }

        Settings.BackupEnabled = BackupToggle.IsChecked == true;
        Settings.Save();
        UpdateBackupUi();

        // Ao ligar, já guarda uma cópia do documento atual (se ele está salvo).
        if (Settings.BackupEnabled && _filePath is not null && !_dirty)
            await RunBackupAsync(_filePath);
    }

    private void ChooseBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!ChooseBackupFolder("Escolha a pasta de backup"))
            return;

        Settings.BackupEnabled = true;
        BackupToggle.IsChecked = true;
        Settings.Save();
        UpdateBackupUi();
    }

    private bool ChooseBackupFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        if (Settings.BackupFolder is { } current && Directory.Exists(current))
            dialog.InitialDirectory = current;

        if (dialog.ShowDialog(this) != true)
            return false;

        Settings.BackupFolder = dialog.FolderName;
        return true;
    }

    private void OpenBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = Settings.BackupFolder;
        if (string.IsNullOrEmpty(folder))
            return;

        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowStatus($"Não foi possível abrir a pasta: {ex.Message}");
        }
    }

    private void BackupKeepBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || BackupKeepBox.SelectedIndex < 0) return;
        Settings.BackupKeep = BackupKeepOptions[BackupKeepBox.SelectedIndex];
        Settings.Save();
    }

    private void UpdateBackupUi()
    {
        var folder = Settings.BackupFolder;
        var inDrive = folder is not null && BackupService.IsInGoogleDrive(folder);

        BackupFolderText.Text = folder is null ? "Nenhuma pasta escolhida" : FriendlyFolderName(folder);
        BackupFolderText.ToolTip = folder;
        BackupFolderIcon.Text = inDrive ? "\uE753" : "\uE8B7"; // nuvem ou pasta
        OpenBackupButton.IsEnabled = folder is not null;

        if (!Settings.BackupEnabled)
        {
            SetBackupStatus("Desativado", ok: null);
            PrivacyNoteText.Text = "Salvo só no seu computador.";
            return;
        }

        PrivacyNoteText.Text = inDrive ? "Cópias de segurança no Google Drive." : "Cópias de segurança na pasta escolhida.";
        if (Settings.LastBackup is { } last)
        {
            var when = last.Date == DateTime.Today ? $"hoje às {last:HH:mm}" : last.ToString("dd/MM 'às' HH:mm", CultureInfo.CurrentCulture);
            SetBackupStatus($"Último backup {when}", ok: true);
        }
        else
        {
            SetBackupStatus("Ativo. A próxima cópia é feita ao salvar.", ok: true);
        }
    }

    /// <summary>Mostra "Google Drive › Anotado Backup" em vez de "G:\Meu Drive\Anotado Backup".</summary>
    private static string FriendlyFolderName(string folder)
    {
        foreach (var marker in new[] { "\\Meu Drive", "\\My Drive", "\\Google Drive" })
        {
            var index = folder.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;
            var rest = folder[(index + marker.Length)..].Trim('\\').Replace("\\", " › ");
            return rest.Length == 0 ? "Google Drive" : $"Google Drive › {rest}";
        }
        return folder;
    }

    /// <summary>ok: true = verde, false = alerta, null = neutro.</summary>
    private void SetBackupStatus(string text, bool? ok)
    {
        BackupStatusText.Text = text;
        BackupDot.SetResourceReference(Shape.FillProperty,
            ok switch { true => "AccentBrush", false => "WarningBrush", null => "MutedBrush" });
    }

    // ================================================================ Painéis, foco e tema

    private void NavToggle_Click(object sender, RoutedEventArgs e)
    {
        Settings.ShowNavigation = !Settings.ShowNavigation;
        Settings.Save();
        ApplyPanelsVisibility();
    }

    private void DocPanelToggle_Click(object sender, RoutedEventArgs e)
    {
        Settings.ShowDocumentPanel = !Settings.ShowDocumentPanel;
        Settings.Save();
        ApplyPanelsVisibility();
    }

    private void ApplyPanelsVisibility()
    {
        NavPanel.Visibility = Settings.ShowNavigation && !_focusMode ? Visibility.Visible : Visibility.Collapsed;
        DocPanel.Visibility = Settings.ShowDocumentPanel && !_focusMode ? Visibility.Visible : Visibility.Collapsed;
        NavToggle.IsChecked = Settings.ShowNavigation;
        DocPanelToggle.IsChecked = Settings.ShowDocumentPanel;
    }

    private void FocusMode_Click(object sender, RoutedEventArgs e) => SetFocusMode(!_focusMode);

    private void SetFocusMode(bool on)
    {
        _focusMode = on;
        TabsBar.Visibility = RibbonBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        FocusLabel.Text = on ? "Sair do foco" : "Modo foco";
        FocusIcon.Text = on ? "" : "";
        ApplyPanelsVisibility();
        if (on) ShowStatus("Modo foco: Esc ou F11 para sair.");
        Editor.Focus();
    }

    private void ThemeLight_Click(object sender, RoutedEventArgs e) => ThemeManager.Apply(AppTheme.Light);
    private void ThemeDark_Click(object sender, RoutedEventArgs e) => ThemeManager.Apply(AppTheme.Dark);
    private void ThemeSystem_Click(object sender, RoutedEventArgs e) => ThemeManager.Apply(AppTheme.System);

    private void PageLight_Click(object sender, RoutedEventArgs e) => ThemeManager.ApplyPage(PageTheme.Light);
    private void PageDark_Click(object sender, RoutedEventArgs e) => ThemeManager.ApplyPage(PageTheme.Dark);
    private void PageApp_Click(object sender, RoutedEventArgs e) => ThemeManager.ApplyPage(PageTheme.FollowApp);

    // ================================================================ Tinta da página

    /// <summary>Cor do texto "automático" quando ele sai do app: arquivo, impressão, área de transferência.</summary>
    private static readonly Brush PrintInk = Frozen(Color.FromRgb(0x26, 0x26, 0x26));

    /// <summary>
    /// O WPF grava no RTF a cor herdada do editor. Com a página escura ela seria clara,
    /// então durante a gravação o editor volta temporariamente à tinta escura.
    /// </summary>
    private void WithPrintInk(Action action)
    {
        if (!ThemeManager.IsPaperDark)
        {
            action();
            return;
        }

        Editor.Foreground = PrintInk;
        try { action(); }
        finally { Editor.SetResourceReference(ForegroundProperty, "PaperInkBrush"); }
    }

    private void Editor_Copying(object sender, DataObjectCopyingEventArgs e)
    {
        if (!ThemeManager.IsPaperDark) return;

        // Regrava os formatos com formatação usando a tinta escura.
        var range = new TextRange(Editor.Selection.Start, Editor.Selection.End);
        WithPrintInk(() =>
        {
            foreach (var format in new[] { DataFormats.Rtf, DataFormats.Xaml, DataFormats.XamlPackage })
            {
                if (!e.DataObject.GetDataPresent(format) || !range.CanSave(format)) continue;
                var buffer = new MemoryStream();
                range.Save(buffer, format);
                e.DataObject.SetData(format, format == DataFormats.XamlPackage
                    ? buffer
                    : Encoding.UTF8.GetString(buffer.ToArray()));
            }
        });
    }

    /// <summary>
    /// Remove cores pretas/quase pretas explícitas (o RTF grava a cor herdada em todo o texto),
    /// para que o texto siga a tinta da página e continue legível na página escura.
    /// </summary>
    private static void NormalizeInk(DependencyObject node)
    {
        if (node is FlowDocument or TextElement
            && node.ReadLocalValue(TextElement.ForegroundProperty) is SolidColorBrush { Color: var c }
            && IsAutomaticInk(c))
        {
            node.ClearValue(TextElement.ForegroundProperty);
        }

        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            NormalizeInk(child);
    }

    private static bool IsAutomaticInk(Color c)
    {
        var max = Math.Max(c.R, Math.Max(c.G, c.B));
        var min = Math.Min(c.R, Math.Min(c.G, c.B));
        return c.A == 255 && max <= 0x30 && max - min <= 8; // preto ou cinza bem escuro, neutro
    }

    private void UpdateThemeButtons()
    {
        ThemeLightButton.IsChecked = ThemeManager.Current == AppTheme.Light;
        ThemeDarkButton.IsChecked = ThemeManager.Current == AppTheme.Dark;
        ThemeSystemButton.IsChecked = ThemeManager.Current == AppTheme.System;
        PageLightButton.IsChecked = ThemeManager.CurrentPage == PageTheme.Light;
        PageDarkButton.IsChecked = ThemeManager.CurrentPage == PageTheme.Dark;
        PageAppButton.IsChecked = ThemeManager.CurrentPage == PageTheme.FollowApp;
    }

    private void SpellToggle_Click(object sender, RoutedEventArgs e) =>
        SpellCheck.SetIsEnabled(Editor, SpellToggle.IsChecked == true);

    private void Shortcuts_Click(object sender, RoutedEventArgs e) => new ShortcutsWindow(this).ShowDialog();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Tab when _suggestion is not null && Keyboard.Modifiers == ModifierKeys.None && Editor.IsKeyboardFocusWithin:
                AcceptSuggestion();
                e.Handled = true;
                break;
            case Key.Escape when _suggestion is not null && Editor.IsKeyboardFocusWithin:
                _suggestionDismissed = true;
                HideSuggestion();
                e.Handled = true;
                break;
            case Key.F1:
                new ShortcutsWindow(this).ShowDialog();
                e.Handled = true;
                break;
            case Key.F11:
                SetFocusMode(!_focusMode);
                e.Handled = true;
                break;
            case Key.Escape when _focusMode && !FindBox.IsKeyboardFocusWithin && !ReplaceBox.IsKeyboardFocusWithin:
                SetFocusMode(false);
                e.Handled = true;
                break;
            case Key.K when Keyboard.Modifiers == ModifierKeys.Control:
                InsertLink_Click(this, e);
                e.Handled = true;
                break;
        }
    }

    // ================================================================ Navegação (estrutura e busca)

    private void BuildOutline()
    {
        var entries = new List<OutlineEntry>();
        foreach (var p in EnumerateParagraphs(Editor.Document.Blocks))
        {
            var kind = DetectKind(p);
            if (kind is not (ParagraphKind.Title or ParagraphKind.Heading1 or ParagraphKind.Heading2))
                continue;

            var text = new TextRange(p.ContentStart, p.ContentEnd).Text.Trim();
            if (text.Length == 0)
                continue;

            var level = kind == ParagraphKind.Heading2 ? 2 : 1;
            entries.Add(new OutlineEntry((entries.Count + 1).ToString("00"), text, level, p));
        }

        // Evita recriar a lista (e perder a seleção) quando nada mudou.
        if (!entries.Select(x => (x.Text, x.Level, x.Paragraph)).SequenceEqual(_outline.Select(x => (x.Text, x.Level, x.Paragraph))))
        {
            _outline = entries;
            OutlineList.ItemsSource = entries;
        }

        OutlineEmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SectionCountText.Text = $"{entries.Count} {(entries.Count == 1 ? "seção" : "seções")}";
        HighlightCurrentSection();
    }

    private void HighlightCurrentSection()
    {
        var caret = Editor.Selection.Start;
        OutlineList.SelectedItem = _outline.LastOrDefault(x => x.Paragraph.ContentStart.CompareTo(caret) <= 0)
                                   ?? _outline.FirstOrDefault();
    }

    private void OutlineList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(OutlineList, (DependencyObject)e.OriginalSource) is not ListBoxItem { DataContext: OutlineEntry entry })
            return;

        Editor.Focus();
        Editor.CaretPosition = entry.Paragraph.ContentStart;
        ScrollToTop(entry.Paragraph.ContentStart);
        e.Handled = true;
    }

    private void NavSearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        SearchPlaceholder.Visibility = NavSearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void NavSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            FindNext(NavSearchBox.Text);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            NavSearchBox.Clear();
            Editor.Focus();
            e.Handled = true;
        }
    }

    // ================================================================ Localizar e substituir

    private void Find_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (_focusMode) SetFocusMode(false);
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
            if (sender == ReplaceBox) ReplaceCurrent(); else FindNext(FindBox.Text);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseFindBar();
            e.Handled = true;
        }
    }

    private void FindNext_Click(object sender, RoutedEventArgs e) => FindNext(FindBox.Text);
    private void Replace_Click(object sender, RoutedEventArgs e) => ReplaceCurrent();
    private void CloseFind_Click(object sender, RoutedEventArgs e) => CloseFindBar();

    private void CloseFindBar()
    {
        FindBar.Visibility = Visibility.Collapsed;
        Editor.Focus();
    }

    private StringComparison Comparison =>
        MatchCaseBox.IsChecked == true ? StringComparison.CurrentCulture : StringComparison.CurrentCultureIgnoreCase;

    private bool FindNext(string term)
    {
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
        if (term.Length == 0 || Editor.IsReadOnly)
            return;

        if (string.Equals(Editor.Selection.Text, term, Comparison))
        {
            Editor.Selection.Text = ReplaceBox.Text;
            Editor.Selection.Select(Editor.Selection.End, Editor.Selection.End);
        }
        FindNext(term);
    }

    private void ReplaceAll_Click(object sender, RoutedEventArgs e)
    {
        var term = FindBox.Text;
        if (term.Length == 0 || Editor.IsReadOnly)
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

    private void ScrollToTop(TextPointer position)
    {
        Editor.UpdateLayout();
        var rect = position.GetCharacterRect(LogicalDirection.Forward);
        if (!rect.IsEmpty)
            Editor.ScrollToVerticalOffset(Math.Max(0, Editor.VerticalOffset + rect.Top - 24));
    }

    // ================================================================ Menu de contexto (ortografia)

    private void Editor_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = Editor.ContextMenu!;
        menu.Items.Clear();

        var position = Editor.GetPositionFromPoint(Mouse.GetPosition(Editor), snapToText: true);
        var error = position is null || Editor.IsReadOnly ? null : Editor.GetSpellingError(position);

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

    // ================================================================ Zoom

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ZoomTransform is null || ZoomText is null) return; // ainda inicializando
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

    // ================================================================ Utilitários

    private static double PtToDip(double pt) => pt * 96.0 / 72.0;
    private static double DipToPt(double dip) => dip * 72.0 / 96.0;

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
