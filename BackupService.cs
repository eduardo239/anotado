using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Anotado;

/// <summary>
/// Cópias de segurança em uma pasta comum. Se a pasta estiver dentro do Google Drive para computador
/// (ou OneDrive, Dropbox...), o próprio programa de sincronização envia as cópias para a nuvem.
/// </summary>
public static class BackupService
{
    private const string StampFormat = "yyyy-MM-dd HH'h'mm";
    private const string StampFormatWithSeconds = "yyyy-MM-dd HH'h'mm'm'ss";

    private static readonly string[] DriveFolderNames = ["Meu Drive", "My Drive"];

    /// <summary>Procura a pasta "Meu Drive" criada pelo Google Drive para computador.</summary>
    public static string? FindGoogleDrive()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType == DriveType.CDRom)
                    continue;
                foreach (var name in DriveFolderNames)
                {
                    var path = System.IO.Path.Combine(drive.RootDirectory.FullName, name);
                    if (Directory.Exists(path))
                        return path;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // Versão antiga ("Backup e sincronização")
        var legacy = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Google Drive");
        return Directory.Exists(legacy) ? legacy : null;
    }

    public static bool IsInGoogleDrive(string folder) =>
        DriveFolderNames.Any(n => folder.Contains($"\\{n}", StringComparison.OrdinalIgnoreCase)) ||
        folder.Contains("\\Google Drive", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Copia o arquivo salvo para &lt;pasta&gt;\&lt;nome do documento&gt;\&lt;nome&gt; (aaaa-mm-dd HHhmm).ext
    /// e apaga as cópias mais antigas além de <paramref name="keep"/> (0 = manter todas).
    /// </summary>
    public static string Backup(string sourceFile, string backupRoot, int keep)
    {
        if (!Directory.Exists(System.IO.Path.GetPathRoot(backupRoot)))
            throw new DirectoryNotFoundException("A unidade da pasta de backup não está disponível.");

        var name = System.IO.Path.GetFileNameWithoutExtension(sourceFile);
        var extension = System.IO.Path.GetExtension(sourceFile);
        var folder = System.IO.Path.Combine(backupRoot, name);
        Directory.CreateDirectory(folder);

        var now = DateTime.Now;
        var target = System.IO.Path.Combine(folder, $"{name} ({now.ToString(StampFormat, CultureInfo.InvariantCulture)}){extension}");
        if (File.Exists(target)) // mais de um salvamento no mesmo minuto
            target = System.IO.Path.Combine(folder, $"{name} ({now.ToString(StampFormatWithSeconds, CultureInfo.InvariantCulture)}){extension}");

        File.Copy(sourceFile, target, overwrite: true);

        if (keep > 0)
            DeleteOldCopies(folder, name, extension, keep);

        return target;
    }

    /// <summary>Apaga apenas arquivos no padrão de nome das cópias deste documento.</summary>
    private static void DeleteOldCopies(string folder, string name, string extension, int keep)
    {
        var pattern = new Regex(
            $@"^{Regex.Escape(name)} \((\d{{4}}-\d{{2}}-\d{{2}} \d{{2}}h\d{{2}}(?:m\d{{2}})?)\){Regex.Escape(extension)}$",
            RegexOptions.IgnoreCase);

        var oldCopies = Directory.EnumerateFiles(folder)
            .Select(path => (Path: path, Match: pattern.Match(System.IO.Path.GetFileName(path))))
            .Where(x => x.Match.Success)
            .OrderByDescending(x => x.Match.Groups[1].Value, StringComparer.Ordinal) // data no nome é ordenável
            .Skip(keep);

        foreach (var (path, _) in oldCopies)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
