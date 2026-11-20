using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Anotado;

/// <summary>
/// Tabelas: seletor de tamanho ao inserir e botões (+)/(−) nas bordas para incluir ou remover
/// colunas (borda de cima) e linhas (borda esquerda) a partir da célula onde está o cursor.
/// </summary>
public partial class MainWindow
{
    private const int PickerRows = 8, PickerColumns = 10;
    private const double HandleSize = 18;

    private static readonly Thickness CellPadding = new(8, 5, 8, 5);
    private static readonly Thickness CellBorder = new(0.75);

    private Button? _colInsertLeft, _colRemove, _colInsertRight, _rowInsertAbove, _rowRemove, _rowInsertBelow;

    // ================================================================ Inserir (seletor colunas × linhas)

    private void InsertTable_Click(object sender, RoutedEventArgs e)
    {
        if (Editor.IsReadOnly) return;

        var (rows, columns) = (3, 3);
        var squares = new Border[PickerRows, PickerColumns];
        var label = new TextBlock { Margin = new Thickness(2, 0, 0, 8), FontWeight = FontWeights.SemiBold };
        var grid = new UniformGrid { Rows = PickerRows, Columns = PickerColumns };
        Popup? popup = null;

        void Select(int r, int c)
        {
            (rows, columns) = (Math.Clamp(r, 1, PickerRows), Math.Clamp(c, 1, PickerColumns));
            for (var i = 0; i < PickerRows; i++)
                for (var j = 0; j < PickerColumns; j++)
                    squares[i, j].SetResourceReference(Border.BackgroundProperty,
                        i < rows && j < columns ? "AccentFillBrush" : "CardBrush");
            label.Text = $"{columns} {(columns == 1 ? "coluna" : "colunas")} × {rows} {(rows == 1 ? "linha" : "linhas")}";
        }

        void Insert()
        {
            popup!.IsOpen = false;
            InsertTable(rows, columns);
        }

        for (var i = 0; i < PickerRows; i++)
        {
            for (var j = 0; j < PickerColumns; j++)
            {
                var square = new Border
                {
                    Width = 18, Height = 18, Margin = new Thickness(1.5),
                    BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2),
                };
                square.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
                var (r, c) = (i + 1, j + 1);
                square.MouseEnter += (_, _) => Select(r, c);
                square.MouseLeftButtonUp += (_, _) => Insert();
                squares[i, j] = square;
                grid.Children.Add(square);
            }
        }

        var hint = new TextBlock { Text = "Setas para ajustar · Enter para inserir", FontSize = 11, Margin = new Thickness(2, 8, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(label);
        panel.Children.Add(grid);
        panel.Children.Add(hint);

        var frame = new Border
        {
            Child = panel, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            Focusable = true, FocusVisualStyle = null,
        };
        frame.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        frame.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
        frame.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
        frame.SetResourceReference(TextElement.FontFamilyProperty, "UiFont");
        frame.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Right: Select(rows, columns + 1); break;
                case Key.Left: Select(rows, columns - 1); break;
                case Key.Down: Select(rows + 1, columns); break;
                case Key.Up: Select(rows - 1, columns); break;
                case Key.Enter: Insert(); break;
                case Key.Escape: popup!.IsOpen = false; break;
                default: return;
            }
            e.Handled = true;
        };

        popup = new Popup
        {
            Child = frame, PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom,
            StaysOpen = false, AllowsTransparency = true, VerticalOffset = 4,
        };
        popup.Opened += (_, _) => Keyboard.Focus(frame);
        popup.Closed += (_, _) => { if (!Editor.IsKeyboardFocusWithin) Editor.Focus(); };
        Select(rows, columns);
        popup.IsOpen = true;
    }

    private void InsertTable(int rows, int columns)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 12) };
        for (var c = 0; c < columns; c++)
            table.Columns.Add(new TableColumn());

        var group = new TableRowGroup();
        for (var r = 0; r < rows; r++)
        {
            var row = new TableRow();
            for (var c = 0; c < columns; c++)
                row.Cells.Add(NewCellLike(null));
            group.Rows.Add(row);
        }
        table.RowGroups.Add(group);

        Editor.BeginChange();
        var current = Editor.CaretPosition.Paragraph;
        if (current?.SiblingBlocks is { } siblings)
            siblings.InsertAfter(current, table);
        else
            Editor.Document.Blocks.Add(table);

        KeepApartFromTables(table); // tabelas encostadas viram uma só no RTF
        Editor.EndChange();

        MoveCaretTo(group.Rows[0].Cells[0]);
    }

    /// <summary>Célula nova com a mesma borda e espaçamento da célula de referência.</summary>
    private static TableCell NewCellLike(TableCell? template) => new(new Paragraph { Margin = new Thickness(0) })
    {
        BorderBrush = template?.BorderBrush ?? TableLine,
        BorderThickness = template?.BorderThickness ?? CellBorder,
        Padding = template?.Padding ?? CellPadding,
    };

    private void MoveCaretTo(TableCell cell)
    {
        Editor.CaretPosition = cell.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        Editor.Focus();
    }

    // ================================================================ Botões (+)/(−) nas bordas

    private TableCell? CurrentCell()
    {
        DependencyObject? node = Editor.Selection.Start.Parent;
        while (node is not null and not TableCell)
            node = LogicalTreeHelper.GetParent(node);
        return node as TableCell;
    }

    private Button MakeHandle(string text, string tooltip, bool remove, Action<TableCell> action)
    {
        var button = new Button
        {
            Content = text,
            ToolTip = tooltip,
            Style = (Style)FindResource(remove ? "TableHandleRemove" : "TableHandle"),
            Visibility = Visibility.Collapsed,
        };
        button.Click += (_, _) =>
        {
            if (CurrentCell() is { } cell && !Editor.IsReadOnly)
                action(cell);
        };
        TableLayer.Children.Add(button);
        return button;
    }

    private void EnsureHandles()
    {
        if (_colRemove is not null) return;
        _colInsertLeft = MakeHandle("+", "Inserir coluna à esquerda", false, c => InsertColumn(c, after: false));
        _colRemove = MakeHandle("−", "Remover esta coluna", true, RemoveColumn);
        _colInsertRight = MakeHandle("+", "Inserir coluna à direita", false, c => InsertColumn(c, after: true));
        _rowInsertAbove = MakeHandle("+", "Inserir linha acima", false, c => InsertRow(c, after: false));
        _rowRemove = MakeHandle("−", "Remover esta linha", true, RemoveRow);
        _rowInsertBelow = MakeHandle("+", "Inserir linha abaixo", false, c => InsertRow(c, after: true));
    }

    private void HideTableHandles()
    {
        // Só os seis botões de tabela (a mesma camada guarda os botões dos blocos de código).
        foreach (var handle in new[] { _colInsertLeft, _colRemove, _colInsertRight, _rowInsertAbove, _rowRemove, _rowInsertBelow })
            if (handle is not null) handle.Visibility = Visibility.Collapsed;
    }

    /// <summary>Posiciona os botões nas bordas da tabela onde está o cursor (ou esconde, fora de tabelas).</summary>
    private void LayoutTableHandles()
    {
        EnsureHandles();
        if (Editor.IsReadOnly || CurrentCell() is not { Parent: TableRow { Parent: TableRowGroup { Parent: Table table } } row } cell
            || IsCodeBlock(table) // bloco de código tem botões próprios
            || Editor.Template.FindName("PART_ContentHost", Editor) is not ScrollViewer host)
        {
            HideTableHandles();
            return;
        }

        var rows = table.RowGroups.SelectMany(g => g.Rows).ToList();
        if (RowBounds(rows[0]) is not { } firstRow || RowBounds(row) is not { } currentRow)
        {
            HideTableHandles();
            return;
        }
        var tableTop = firstRow.Top;
        var (rowTop, rowBottom) = currentRow;

        // Colunas: o documento não tem margem interna (PagePadding = 0), então a tabela ocupa
        // a largura visível do editor; as larguras seguem as definições das colunas.
        var left = Editor.Padding.Left + table.Margin.Left;
        var right = Editor.Padding.Left + host.ViewportWidth - table.Margin.Right;
        var edges = ColumnEdges(table, rows, left, right);
        var column = Math.Min(row.Cells.IndexOf(cell), edges.Length - 2);
        if (column < 0)
        {
            HideTableHandles();
            return;
        }
        var (colLeft, colRight) = (edges[column], edges[column + 1]);

        // Botões de coluna acima da tabela; de linha à esquerda dela (o (−) mais afastado, para não encostar nos (+)).
        var columnsY = tableTop - 12;
        Place(_colInsertLeft!, colLeft, columnsY);
        Place(_colRemove!, (colLeft + colRight) / 2, columnsY);
        Place(_colInsertRight!, colRight, columnsY);

        var rowsX = left - 12;
        Place(_rowInsertAbove!, rowsX, rowTop);
        Place(_rowRemove!, rowsX - 22, (rowTop + rowBottom) / 2);
        Place(_rowInsertBelow!, rowsX, rowBottom);
    }

    private void Place(Button handle, double centerX, double centerY)
    {
        var point = Editor.TranslatePoint(new Point(centerX, centerY), TableLayer);
        Canvas.SetLeft(handle, point.X - HandleSize / 2);
        Canvas.SetTop(handle, point.Y - HandleSize / 2);
        handle.Visibility = Visibility.Visible;
    }

    /// <summary>Topo e base da linha (bordas incluídas), medidos pelo texto das células.</summary>
    private static (double Top, double Bottom)? RowBounds(TableRow row)
    {
        double top = double.MaxValue, bottom = double.MinValue;
        foreach (var cell in row.Cells)
        {
            if (cell.Blocks.FirstBlock is not { } first || cell.Blocks.LastBlock is not { } last) continue;
            var start = first.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            var end = last.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
            if (start.IsEmpty || end.IsEmpty) continue;
            top = Math.Min(top, start.Top - first.Margin.Top - cell.Padding.Top - cell.BorderThickness.Top);
            bottom = Math.Max(bottom, end.Bottom + last.Margin.Bottom + cell.Padding.Bottom + cell.BorderThickness.Bottom);
        }
        return top == double.MaxValue ? null : (top, bottom);
    }

    /// <summary>
    /// Posição das linhas verticais da tabela, seguindo a largura das colunas
    /// (fixa em pixels ou proporcional, como o WPF distribui).
    /// </summary>
    private static double[] ColumnEdges(Table table, List<TableRow> rows, double left, double right)
    {
        var count = Math.Max(table.Columns.Count, rows.Max(r => r.Cells.Sum(c => Math.Max(1, c.ColumnSpan))));
        var widths = new double[count];
        double fixedTotal = 0, starTotal = 0;
        for (var i = 0; i < count; i++)
        {
            var width = i < table.Columns.Count ? table.Columns[i].Width : new GridLength(1, GridUnitType.Star);
            if (width.IsAbsolute) { widths[i] = width.Value; fixedTotal += width.Value; }
            else starTotal += width.IsStar ? width.Value : 1;
        }
        var free = Math.Max(0, right - left - fixedTotal);
        for (var i = 0; i < count; i++)
        {
            var width = i < table.Columns.Count ? table.Columns[i].Width : new GridLength(1, GridUnitType.Star);
            if (!width.IsAbsolute) widths[i] = starTotal > 0 ? free * (width.IsStar ? width.Value : 1) / starTotal : 0;
        }

        var edges = new double[count + 1];
        edges[0] = left;
        for (var i = 0; i < count; i++)
            edges[i + 1] = edges[i] + widths[i];
        return edges;
    }

    // ================================================================ Operações

    /// <summary>Depois de incluir/remover colunas, todas passam a ter a mesma largura.</summary>
    private static void EqualizeColumns(Table table, int count)
    {
        table.Columns.Clear();
        for (var i = 0; i < count; i++)
            table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
    }

    private void InsertColumn(TableCell current, bool after)
    {
        var row = (TableRow)current.Parent;
        var table = (Table)((TableRowGroup)row.Parent).Parent;
        var index = row.Cells.IndexOf(current) + (after ? 1 : 0);

        Editor.BeginChange();
        foreach (var r in table.RowGroups.SelectMany(g => g.Rows))
        {
            var at = Math.Min(index, r.Cells.Count);
            var template = r.Cells.Count == 0 ? null : r.Cells[Math.Min(at, r.Cells.Count - 1)];
            r.Cells.Insert(at, NewCellLike(template));
        }
        EqualizeColumns(table, table.RowGroups.SelectMany(g => g.Rows).Max(r => r.Cells.Count));
        Editor.EndChange();

        MoveCaretTo(row.Cells[Math.Min(index, row.Cells.Count - 1)]);
    }

    private void RemoveColumn(TableCell current)
    {
        var row = (TableRow)current.Parent;
        var table = (Table)((TableRowGroup)row.Parent).Parent;
        var allRows = table.RowGroups.SelectMany(g => g.Rows).ToList();
        var index = row.Cells.IndexOf(current);

        if (allRows.Max(r => r.Cells.Count) <= 1)
        {
            RemoveTable(table);
            return;
        }

        Editor.BeginChange();
        foreach (var r in allRows)
            if (index < r.Cells.Count && r.Cells.Count > 1)
                r.Cells.RemoveAt(index);
        EqualizeColumns(table, allRows.Max(r => r.Cells.Count));
        Editor.EndChange();

        MoveCaretTo(row.Cells[Math.Min(index, row.Cells.Count - 1)]);
    }

    private void InsertRow(TableCell current, bool after)
    {
        var row = (TableRow)current.Parent;
        var group = (TableRowGroup)row.Parent;
        var column = row.Cells.IndexOf(current);

        var newRow = new TableRow();
        foreach (var cell in row.Cells)
        {
            var copy = NewCellLike(cell);
            copy.ColumnSpan = cell.ColumnSpan;
            newRow.Cells.Add(copy);
        }

        Editor.BeginChange();
        group.Rows.Insert(group.Rows.IndexOf(row) + (after ? 1 : 0), newRow);
        Editor.EndChange();

        MoveCaretTo(newRow.Cells[column]);
    }

    private void RemoveRow(TableCell current)
    {
        var row = (TableRow)current.Parent;
        var group = (TableRowGroup)row.Parent;
        var table = (Table)group.Parent;
        var column = row.Cells.IndexOf(current);

        if (table.RowGroups.Sum(g => g.Rows.Count) <= 1)
        {
            RemoveTable(table);
            return;
        }

        var allRows = table.RowGroups.SelectMany(g => g.Rows).ToList();
        var position = allRows.IndexOf(row);

        Editor.BeginChange();
        group.Rows.Remove(row);
        if (group.Rows.Count == 0)
            table.RowGroups.Remove(group);
        Editor.EndChange();

        allRows.Remove(row);
        var target = allRows[Math.Min(position, allRows.Count - 1)];
        MoveCaretTo(target.Cells[Math.Min(column, target.Cells.Count - 1)]);
    }

    /// <summary>Remover a última coluna ou linha remove a tabela inteira.</summary>
    private void RemoveTable(Table table)
    {
        Editor.BeginChange();
        var siblings = table.SiblingBlocks!;
        var next = table.NextBlock ?? table.PreviousBlock;
        siblings.Remove(table);
        if (next is null)
        {
            next = new Paragraph();
            siblings.Add(next);
        }
        Editor.EndChange();

        Editor.CaretPosition = next.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        Editor.Focus();
    }
}
