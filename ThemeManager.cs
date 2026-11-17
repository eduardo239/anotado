using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Microsoft.Win32;

namespace Anotado;

public enum AppTheme { System, Light, Dark }

/// <summary>Tema da página (papel) onde o texto é escrito, independente do tema do app.</summary>
public enum PageTheme { Light, Dark, FollowApp }

/// <summary>
/// Aplica o tema claro/escuro: controles padrão via tema Fluent do WPF
/// e cores próprias do Anotado via Themes/Light.xaml ou Themes/Dark.xaml.
/// A página tem tema próprio: se escura, Themes/PaperDark.xaml sobrepõe as cores de papel.
/// </summary>
public static class ThemeManager
{
    private static ResourceDictionary? _appColors;
    private static ResourceDictionary? _paperColors;

    public static AppTheme Current { get; private set; } = AppTheme.System;
    public static PageTheme CurrentPage { get; private set; } = PageTheme.Light;

    public static event Action? ThemeChanged;

    public static void Initialize()
    {
        CurrentPage = AppSettings.Current.PageTheme;
        Apply(AppSettings.Current.Theme, save: false);

        // Em "Seguir o Windows", acompanha mudanças feitas com o app aberto.
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && Current == AppTheme.System)
                Application.Current.Dispatcher.Invoke(() => Apply(AppTheme.System, save: false));
        };
    }

    public static void Apply(AppTheme theme, bool save = true)
    {
        Current = theme;

#pragma warning disable WPF0001 // ThemeMode ainda é marcado como experimental no WPF
        Application.Current.ThemeMode = theme switch
        {
            AppTheme.Light => ThemeMode.Light,
            AppTheme.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
#pragma warning restore WPF0001

        if (save)
        {
            AppSettings.Current.Theme = theme;
            AppSettings.Current.Save();
        }
        ReloadColors();
    }

    public static void ApplyPage(PageTheme theme)
    {
        CurrentPage = theme;
        AppSettings.Current.PageTheme = theme;
        AppSettings.Current.Save();
        ReloadColors();
    }

    private static void ReloadColors()
    {
        var merged = Application.Current.Resources.MergedDictionaries;
        if (_appColors is not null) merged.Remove(_appColors);
        if (_paperColors is not null) merged.Remove(_paperColors);

        _appColors = Load(IsDark ? "Dark" : "Light");
        merged.Add(_appColors);

        // Adicionado depois, para sobrepor as cores de papel do tema do app.
        _paperColors = IsPaperDark ? Load("PaperDark") : null;
        if (_paperColors is not null) merged.Add(_paperColors);

        ThemeChanged?.Invoke();
    }

    private static ResourceDictionary Load(string name) =>
        new() { Source = new Uri($"pack://application:,,,/Anotado;component/Themes/{name}.xaml") };

    public static bool IsDark => Current == AppTheme.Dark || (Current == AppTheme.System && IsWindowsDark());

    public static bool IsPaperDark => CurrentPage == PageTheme.Dark || (CurrentPage == PageTheme.FollowApp && IsDark);

    private static bool IsWindowsDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
    }
}

/// <summary>True quando o valor não é nulo (usado para detectar trechos com fundo, como realces).</summary>
public sealed class NotNullConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not null;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
