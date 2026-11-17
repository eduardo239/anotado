using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Anotado;

/// <summary>Base das janelas auxiliares: segue as cores do tema atual.</summary>
public abstract class ThemedDialog : Window
{
    protected ThemedDialog(Window owner, string title)
    {
        Owner = owner;
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = (System.Windows.Media.FontFamily)FindResource("UiFont");
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "PanelBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
    }

    protected Button MakeButton(string text, string styleKey) => new()
    {
        Content = text,
        Style = (Style)FindResource(styleKey),
        Margin = new Thickness(8, 0, 0, 0),
        MinWidth = 90,
    };
}

/// <summary>Pergunta um texto ao usuário (ex.: endereço de um link).</summary>
public sealed class InputDialog : ThemedDialog
{
    private readonly TextBox _input;

    private InputDialog(Window owner, string title, string prompt, string initial) : base(owner, title)
    {
        _input = new TextBox { Text = initial, Width = 380, Margin = new Thickness(0, 10, 0, 22) };

        var ok = MakeButton("OK", "PrimaryButton");
        ok.IsDefault = true;
        ok.Click += (_, _) => DialogResult = true;
        var cancel = MakeButton("Cancelar", "GhostButton");
        cancel.IsCancel = true;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        root.Children.Add(new TextBlock { Text = prompt });
        root.Children.Add(_input);
        root.Children.Add(buttons);
        Content = root;

        Loaded += (_, _) => { _input.Focus(); _input.SelectAll(); };
    }

    public static string? Ask(Window owner, string title, string prompt, string initial = "")
    {
        var dialog = new InputDialog(owner, title, prompt, initial);
        return dialog.ShowDialog() == true ? dialog._input.Text.Trim() : null;
    }
}

/// <summary>Lista de atalhos de teclado (F1).</summary>
public sealed class ShortcutsWindow : ThemedDialog
{
    private static readonly (string Keys, string Action)[] Shortcuts =
    [
        ("Ctrl+N", "Novo documento"),
        ("Ctrl+O", "Abrir"),
        ("Ctrl+S", "Salvar"),
        ("Ctrl+Shift+S", "Salvar como"),
        ("Ctrl+P", "Imprimir / gerar PDF"),
        ("Ctrl+Z  ·  Ctrl+Y", "Desfazer  ·  Refazer"),
        ("Ctrl+B  ·  Ctrl+I  ·  Ctrl+U", "Negrito  ·  Itálico  ·  Sublinhado"),
        ("Ctrl+L  ·  E  ·  R  ·  J", "Alinhar à esquerda, centro, direita, justificar"),
        ("Ctrl+]  ·  Ctrl+[", "Aumentar  ·  diminuir a fonte"),
        ("Ctrl+K", "Inserir link"),
        ("Tab  ·  Esc", "Aceitar  ·  dispensar sugestão de palavra"),
        ("Ctrl+clique", "Abrir um link"),
        ("Ctrl+F  ·  Ctrl+H", "Localizar  ·  Substituir"),
        ("Ctrl + roda do mouse", "Zoom"),
        ("F11", "Entrar ou sair do modo foco"),
        ("F1", "Esta lista de atalhos"),
    ];

    public ShortcutsWindow(Window owner) : base(owner, "Atalhos de teclado")
    {
        var grid = new Grid { Margin = new Thickness(28, 24, 28, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        for (var i = 0; i < Shortcuts.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });

            var keys = new TextBlock
            {
                Text = Shortcuts[i].Keys,
                FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 32, 0),
            };
            keys.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            Grid.SetRow(keys, i);
            grid.Children.Add(keys);

            var action = new TextBlock { Text = Shortcuts[i].Action, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(action, i);
            Grid.SetColumn(action, 1);
            grid.Children.Add(action);
        }

        var close = MakeButton("Fechar", "GhostButton");
        close.IsCancel = true;
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Margin = new Thickness(0, 0, 28, 22);

        var root = new StackPanel();
        root.Children.Add(grid);
        root.Children.Add(close);
        Content = root;

        KeyDown += (_, e) => { if (e.Key == Key.F1) Close(); };
    }
}
