using System.Windows;
using Microsoft.Win32;

namespace Anotado;

public enum AppTheme { System, Light, Dark }

/// <summary>
/// Aplica o tema claro/escuro: controles padrão via tema Fluent do WPF
/// e cores próprias do Anotado via Themes/Light.xaml ou Themes/Dark.xaml.
/// </summary>
public static class ThemeManager
{
    private static ResourceDictionary? _appColors;

    public static AppTheme Current { get; private set; } = AppTheme.System;

    public static event Action? ThemeChanged;

    public static void Initialize()
    {
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
        var app = Application.Current;
        Current = theme;

#pragma warning disable WPF0001 // ThemeMode ainda é marcado como experimental no WPF
        app.ThemeMode = theme switch
        {
            AppTheme.Light => ThemeMode.Light,
            AppTheme.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
#pragma warning restore WPF0001

        var colors = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Anotado;component/Themes/{(IsDark ? "Dark" : "Light")}.xaml"),
        };
        if (_appColors is not null)
            app.Resources.MergedDictionaries.Remove(_appColors);
        app.Resources.MergedDictionaries.Add(colors);
        _appColors = colors;

        if (save)
        {
            AppSettings.Current.Theme = theme;
            AppSettings.Current.Save();
        }

        ThemeChanged?.Invoke();
    }

    public static bool IsDark => Current == AppTheme.Dark || (Current == AppTheme.System && IsWindowsDark());

    private static bool IsWindowsDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
    }
}
