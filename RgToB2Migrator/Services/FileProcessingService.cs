using Microsoft.Extensions.Logging;
using SharpCompress.Archives;
using SharpCompress.Common;
using System.IO.Compression;
using System.Text;

namespace RgToB2Migrator.Services;

/// <summary>
/// Extracts downloaded archives and processes all contained files:
/// - Non-text files are renamed to scandal69{N}.{ext} (N increments per-post across all archives)
/// - Text files (.txt, .nfo, .url, .html, .htm, .rtf, .log) are renamed to scandal69.com
///   and their content is replaced with the scandal69 promotional text
/// - Preserves subfolder structure for B2 upload paths
/// </summary>
public class FileProcessingService
{
    private readonly ILogger<FileProcessingService> _logger;

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz",
        ".tar.gz", ".tar.bz2", ".tar.xz", ".tgz", ".tbz2"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".nfo", ".url", ".html", ".htm", ".rtf", ".log", ".diz"
    };

    private const string TextContent =
        "Amateyr Leaks US UK CA AU and SEX Scandal Collection\r\nhttps://www.scandal69.com";

    public record ProcessedFile(string RelativePath, string LocalPath);

    /// <summary>Thread-safe counter passed between async tasks.</summary>
    public sealed class FileCounter
    {
        private int _value = 1;
        public int Value => _value;
        public int Increment() => Interlocked.Increment(ref _value);
    }

    private readonly object _processingLock = new();

    public FileProcessingService(ILogger<FileProcessingService> logger)
    {
        _logger = logger;
    }

    // ──────────────────────────────────────────────────────────
    //  Public: determine whether a file is an archive
    // ──────────────────────────────────────────────────────────

    public bool IsArchive(string filePath)
    {
        var name = Path.GetFileName(filePath);

        // Handle compound extensions like .tar.gz
        if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".tar.bz2", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase))
            return true;

        return ArchiveExtensions.Contains(Path.GetExtension(filePath));
    }

    // ──────────────────────────────────────────────────────────
    //  Public: extract archive to a folder
    // ──────────────────────────────────────────────────────────

    public async Task<string> ExtractArchiveAsync(string archivePath, string extractionRoot, CancellationToken ct)
    {
        var baseName = Path.GetFileNameWithoutExtension(archivePath);
        // Strip .tar from .tar.gz etc.
        if (baseName.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
            baseName = Path.GetFileNameWithoutExtension(baseName);

        // Sanitize for use as a folder name
        baseName = SanitizeForPath(baseName);

        var extractionFolder = Path.Combine(extractionRoot, baseName + "_extracted");

        // Clean up any leftover folder from a previous failed attempt
        if (Directory.Exists(extractionFolder))
            Directory.Delete(extractionFolder, recursive: true);

        Directory.CreateDirectory(extractionFolder);

        _logger.LogInformation("Extracting {Archive} → {Folder}", archivePath, extractionFolder);

        await Task.Run(() =>
        {
            try
            {
                using var archive = ArchiveFactory.Open(archivePath);
                int entryCount = 0;

                foreach (var entry in archive.Entries)
                {
                    ct.ThrowIfCancellationRequested();

                    if (entry.IsDirectory) continue;

                    entry.WriteToDirectory(extractionFolder, new ExtractionOptions
                    {
                        ExtractFullPath = true,
                        Overwrite = true,
                        PreserveAttributes = false
                    });
                    entryCount++;
                }

                _logger.LogInformation("Extracted {Count} files from {Archive}", entryCount, Path.GetFileName(archivePath));
            }
            catch (Exception ex) when (ex.Message.Contains("password") || ex.Message.Contains("encrypted"))
            {
                throw new InvalidOperationException($"Archive is password-protected: {Path.GetFileName(archivePath)}", ex);
            }
        }, ct);

        return extractionFolder;
    }

    // ──────────────────────────────────────────────────────────
    //  Public: process all files in extraction folder
    //  counter is updated in-place (pass by ref from orchestrator)
    // ──────────────────────────────────────────────────────────

    public List<ProcessedFile> ProcessExtractedFiles(string extractionFolder, FileCounter counter)
    {
        var results = new List<ProcessedFile>();

        // Track text file names per directory to handle collisions
        var textFileCountPerDir = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        ProcessDirectory(extractionFolder, extractionFolder, counter, textFileCountPerDir, results);

        _logger.LogInformation("Processed {Count} files in {Folder}", results.Count, extractionFolder);
        return results;
    }

    // ──────────────────────────────────────────────────────────
    //  Public: pack processed files into a single ZIP
    // ──────────────────────────────────────────────────────────

    public async Task CreateZipAsync(
        List<ProcessedFile> files, string zipPath, CancellationToken ct)
    {
        _logger.LogInformation("Creating ZIP {ZipPath} with {Count} file(s)", zipPath, files.Count);

        await Task.Run(() =>
        {
            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                // Use RelativePath as the entry name so subfolder structure is preserved inside the zip
                zip.CreateEntryFromFile(file.LocalPath, file.RelativePath, CompressionLevel.NoCompression);
            }
        }, ct);
    }

    // ──────────────────────────────────────────────────────────
    //  Public: handle a single non-archive file (no extraction)
    // ──────────────────────────────────────────────────────────

    public ProcessedFile ProcessSingleFile(string filePath, string extractionRoot, FileCounter counter)
    {
        var dir = Path.GetDirectoryName(filePath) ?? extractionRoot;
        var textFileCountPerDir = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        return RenameAndProcess(filePath, extractionRoot, dir, counter, textFileCountPerDir);
    }

    // ──────────────────────────────────────────────────────────
    //  Private: recursively walk directories
    // ──────────────────────────────────────────────────────────

    private void ProcessDirectory(
        string currentDir,
        string rootDir,
        FileCounter counter,
        Dictionary<string, int> textFileCountPerDir,
        List<ProcessedFile> results)
    {
        // Process files in this directory first
        foreach (var filePath in Directory.GetFiles(currentDir).OrderBy(f => f))
        {
            var processed = RenameAndProcess(filePath, rootDir, currentDir, counter, textFileCountPerDir);
            results.Add(processed);
        }

        // Then recurse into subdirectories
        foreach (var subDir in Directory.GetDirectories(currentDir).OrderBy(d => d))
        {
            ProcessDirectory(subDir, rootDir, counter, textFileCountPerDir, results);
        }
    }

    // ──────────────────────────────────────────────────────────
    //  Private: rename one file and process its content
    // ──────────────────────────────────────────────────────────

    private ProcessedFile RenameAndProcess(
        string originalPath,
        string rootDir,
        string fileDir,
        FileCounter counter,
        Dictionary<string, int> textFileCountPerDir)
    {
        lock (_processingLock)
        {
            if (IsTextFile(originalPath))
            {
                // Determine collision-free name within this directory
                var key = fileDir;
                if (!textFileCountPerDir.TryGetValue(key, out int textCount))
                    textCount = 0;

                newFileName = textCount == 0
                    ? "scandal69.txt"
                    : $"scandal69({textCount + 1}).txt";

                textFileCountPerDir[key] = textCount + 1;

                // Write replacement content (creates or overwrites the destination file)
                var newPath = Path.Combine(fileDir, newFileName);
                File.WriteAllText(newPath, TextContent, Encoding.UTF8);

                // Delete the original if it was renamed
                if (!string.Equals(originalPath, newPath, StringComparison.OrdinalIgnoreCase))
                    File.Delete(originalPath);

                _logger.LogDebug("Text file: {Original} → {New}", Path.GetFileName(originalPath), newFileName);
            }
            else
            {
                // Rename to scandal69{N}.ext — detect type from magic bytes if no extension
                var ext = Path.GetExtension(originalPath);
                if (string.IsNullOrEmpty(ext))
                    ext = DetectExtension(originalPath);

                // Safely get a unique counter value
                int currentVal = counter.Increment() - 1;
                newFileName = $"scandal69{currentVal}{ext}";

                var newPath = Path.Combine(fileDir, newFileName);

                // Guard against collision (shouldn't happen with per-post counter, but be safe)
                while (File.Exists(newPath) && !string.Equals(newPath, originalPath, StringComparison.OrdinalIgnoreCase))
                {
                    currentVal = counter.Increment() - 1;
                    newFileName = $"scandal69{currentVal}{ext}";
                    newPath = Path.Combine(fileDir, newFileName);
                }

                if (!string.Equals(originalPath, newPath, StringComparison.OrdinalIgnoreCase))
                    File.Move(originalPath, newPath, overwrite: false);

                _logger.LogDebug("File: {Original} → {New}", Path.GetFileName(originalPath), newFileName);
            }
        }

        // Build relative path from root for B2 upload (use forward slashes)
        var finalPath = Path.Combine(fileDir, newFileName);
        var relativePath = Path.GetRelativePath(rootDir, finalPath)
                               .Replace(Path.DirectorySeparatorChar, '/');

        return new ProcessedFile(relativePath, finalPath);
    }

    // ──────────────────────────────────────────────────────────
    //  Private: helpers
    // ──────────────────────────────────────────────────────────

    private static bool IsTextFile(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return TextExtensions.Contains(ext);
    }

    /// <summary>
    /// Returns the correct file extension (with dot) by reading the file's magic bytes.
    /// Falls back to the original extension if the type cannot be detected.
    /// </summary>
    public static string DetectExtension(string filePath)
    {
        var original = Path.GetExtension(filePath);

        try
        {
            Span<byte> header = stackalloc byte[12];
            using var fs = File.OpenRead(filePath);
            var read = fs.Read(header);
            if (read < 4) return original;

            // JPEG
            if (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
                return ".jpg";
            // PNG
            if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
                return ".png";
            // GIF
            if (header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46)
                return ".gif";
            // WebP (RIFF....WEBP)
            if (read >= 12 &&
                header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 &&
                header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50)
                return ".webp";
            // MP4 / MOV (ftyp box at offset 4)
            if (read >= 8 &&
                header[4] == 0x66 && header[5] == 0x74 && header[6] == 0x79 && header[7] == 0x70)
                return ".mp4";
            // AVI (RIFF....AVI )
            if (read >= 12 &&
                header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 &&
                header[8] == 0x41 && header[9] == 0x56 && header[10] == 0x49 && header[11] == 0x20)
                return ".avi";
            // MKV (EBML)
            if (header[0] == 0x1A && header[1] == 0x45 && header[2] == 0xDF && header[3] == 0xA3)
                return ".mkv";
            // MOV (wide/free/mdat/moov boxes — alternative ftyp-less MOV)
            if (read >= 8 &&
                ((header[4] == 0x6D && header[5] == 0x6F && header[6] == 0x6F && header[7] == 0x76) ||
                 (header[4] == 0x66 && header[5] == 0x72 && header[6] == 0x65 && header[7] == 0x65) ||
                 (header[4] == 0x6D && header[5] == 0x64 && header[6] == 0x61 && header[7] == 0x74)))
                return ".mov";
            // WMV / WMA (ASF)
            if (header[0] == 0x30 && header[1] == 0x26 && header[2] == 0xB2 && header[3] == 0x75)
                return ".wmv";

            // ── Archive formats ──────────────────────────────────────────────
            // ZIP (PK\x03\x04)
            if (header[0] == 0x50 && header[1] == 0x4B && header[2] == 0x03 && header[3] == 0x04)
                return ".zip";
            // RAR5 (Rar!\x1A\x07\x01\x00) or RAR4 (Rar!\x1A\x07\x00)
            if (header[0] == 0x52 && header[1] == 0x61 && header[2] == 0x72 && header[3] == 0x21)
                return ".rar";
            // 7-Zip (7z\xBC\xAF\x27\x1C)
            if (header[0] == 0x37 && header[1] == 0x7A && header[2] == 0xBC && header[3] == 0xAF)
                return ".7z";
            // Gzip
            if (header[0] == 0x1F && header[1] == 0x8B)
                return ".gz";
        }
        catch { /* ignore read errors, fall back to original extension */ }

        return original;
    }

    private static string SanitizeForPath(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString();
    }
}
