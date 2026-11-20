using System.IO;
using System.Text.Json;

namespace Anotado;

/// <summary>Preferências do usuário, salvas em %APPDATA%\Anotado\config.json.</summary>
public sealed class AppSettings
{
    private static readonly string FilePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Anotado", "config.json");

    public static AppSettings Current { get; } = Load();

    public AppTheme Theme { get; set; } = AppTheme.System;
    public PageTheme PageTheme { get; set; } = PageTheme.Light;
    public FontPreset FontPreset { get; set; } = FontPreset.SansSerif; // conjunto usado em documentos novos
    public int WordGoal { get; set; } = 500;
    public PageFormat PageFormat { get; set; } = PageFormat.A4;
    public bool Landscape { get; set; }
    public PageMargins Margins { get; set; } = PageMargins.Normal;
    public bool ShowNavigation { get; set; } = true;
    public bool ShowDocumentPanel { get; set; } = true;
    public bool AutoComplete { get; set; } = true;

    // Backup automático: a cada salvamento, uma cópia com data/hora vai para esta pasta
    // (ex.: uma pasta dentro do Google Drive para computador).
    public bool BackupEnabled { get; set; }
    public string? BackupFolder { get; set; }
    public int BackupKeep { get; set; } = 30; // 0 = manter todas
    public DateTime? LastBackup { get; set; }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

public enum PageFormat { A4, Carta, Oficio }

/// <summary>Conjunto de fontes do documento: vale para o texto e para os títulos.</summary>
public enum FontPreset { SansSerif, Serif, Monospace }

public enum PageMargins { Estreita, Normal, Larga }

public static class PageSetup
{
    private const double DipPerMm = 96.0 / 25.4;

    /// <summary>Largura e altura em DIPs (1/96 pol.), já considerando a orientação.</summary>
    public static (double Width, double Height) Size(PageFormat format, bool landscape)
    {
        var (w, h) = format switch
        {
            PageFormat.Carta => (215.9, 279.4),
            PageFormat.Oficio => (215.9, 355.6),
            _ => (210.0, 297.0),
        };
        return landscape ? (h * DipPerMm, w * DipPerMm) : (w * DipPerMm, h * DipPerMm);
    }

    public static double Margin(PageMargins margins) => margins switch
    {
        PageMargins.Estreita => 12.7 * DipPerMm,
        PageMargins.Larga => 31.8 * DipPerMm,
        _ => 25.0 * DipPerMm,
    };
}
