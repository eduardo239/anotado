using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace Anotado;

public enum AppTheme { System, Light, Dark }

/// <summary>
/// Aplica o tema claro/escuro (controles via tema Fluent do WPF + cores próprias do app)
/// e guarda a escolha do usuário em %APPDATA%\Anotado\tema.txt.
/// </summary>
public static class ThemeManager
{
    private static readonly string SettingsFile = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Anotado", "tema.txt");

    private static ResourceDictionary? _appColors;

    public static AppTheme Current { get; private set; } = AppTheme.System;

    public static event Action? ThemeChanged;

    public static void Initialize()
    {
        Apply(LoadSaved(), save: false);

        // Se o usuário escolheu "Seguir o Windows", acompanha mudanças feitas com o app aberto.
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

        var dark = theme == AppTheme.Dark || (theme == AppTheme.System && IsWindowsDark());
        var colors = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Anotado;component/Themes/{(dark ? "Dark" : "Light")}.xaml"),
        };

        if (_appColors is not null)
            app.Resources.MergedDictionaries.Remove(_appColors);
        app.Resources.MergedDictionaries.Add(colors);
        _appColors = colors;

        if (save)
            Save(theme);

        ThemeChanged?.Invoke();
    }

    private static bool IsWindowsDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
    }

    private static AppTheme LoadSaved()
    {
        try
        {
            if (File.Exists(SettingsFile) &&
                Enum.TryParse<AppTheme>(File.ReadAllText(SettingsFile).Trim(), out var theme))
                return theme;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return AppTheme.System;
    }

    private static void Save(AppTheme theme)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, theme.ToString());
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
