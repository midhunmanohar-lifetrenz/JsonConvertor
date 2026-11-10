using System.Text;
using System.Text.Json;
using JSONConverterPro.Core.Models;

namespace JSONConverterPro.Core.Services;

/// <summary>File access, validation, output naming and translation of low-level errors into plain-language messages.</summary>
public sealed class FileService
{
    public static readonly string[] SupportedExtensions = { ".json", ".txt" };

    public bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public void ValidateInput(string path)
    {
        if (!File.Exists(path)) throw new UserFacingException("The file could not be found. It may have been moved or deleted.");
        if (!IsSupported(path)) throw new UserFacingException("This file type is not supported. Choose a .json or .txt file.");
        if (new FileInfo(path).Length == 0) throw new UserFacingException("This file is empty. Choose a file that contains JSON data.");
    }

    /// <summary>Opens a read stream that always yields UTF-8 (UTF-16/32 files are transcoded on the fly).</summary>
    public Stream OpenRead(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        try
        {
            Span<byte> head = stackalloc byte[4];
            int n = fs.Read(head);
            fs.Position = 0;
            Encoding? enc = null;
            if (n >= 4 && head[0] == 0xFF && head[1] == 0xFE && head[2] == 0 && head[3] == 0) enc = new UTF32Encoding(false, true);
            else if (n >= 2 && head[0] == 0xFF && head[1] == 0xFE) enc = Encoding.Unicode;
            else if (n >= 2 && head[0] == 0xFE && head[1] == 0xFF) enc = Encoding.BigEndianUnicode;
            if (enc != null)
            {
                fs.Position = enc.GetPreamble().Length;
                return Encoding.CreateTranscodingStream(fs, enc, Encoding.UTF8);
            }
            return fs;
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    public string EnsureOutputDirectory(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) throw new UserFacingException("Choose an output folder first.");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string SanitizeFileName(string name)
    {
        foreach (char ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        return name.Trim();
    }

    public void CheckDiskSpace(string dir, long requiredBytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(dir));
            if (string.IsNullOrEmpty(root)) return;
            var drive = new DriveInfo(root);
            if (drive.IsReady && drive.AvailableFreeSpace < requiredBytes)
                throw new UserFacingException("There isn't enough free disk space for this export. Free up space or choose another folder.");
        }
        catch (UserFacingException) { throw; }
        catch { /* unknown drive layouts: let the write itself report problems */ }
    }

    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    /// <summary>Converts any exception into a message that is safe and useful for end users.</summary>
    public string Describe(Exception ex)
    {
        switch (ex)
        {
            case UserFacingException u: return u.Message;
            case OperationCanceledException: return "Conversion was cancelled.";
            case JsonException j: return JsonErrors.Wrap(j).Message;
            case UnauthorizedAccessException: return "Permission denied. You don't have access to this file or folder. Choose another location.";
            case DirectoryNotFoundException: return "The output folder could not be found. Choose a different folder.";
            case PathTooLongException: return "The file path is too long. Choose a shorter folder or file name.";
            case FileNotFoundException: return "The file could not be found. It may have been moved or deleted.";
            case IOException io:
            {
                int code = io.HResult & 0xFFFF;
                string m = io.Message ?? "";
                if (code is 112 or 39 || m.Contains("No space left", StringComparison.OrdinalIgnoreCase) || m.Contains("not enough space", StringComparison.OrdinalIgnoreCase))
                    return "There isn't enough free disk space for this export. Free up space or choose another folder.";
                if (code is 32 or 33 || m.Contains("being used by another process", StringComparison.OrdinalIgnoreCase))
                    return "The file is open in another program (for example Excel). Close it and try again.";
                return "The file could not be read or written. Check that it isn't in use and that you have access.";
            }
            case OutOfMemoryException: return "The file is too large to process with the current settings. Try CSV output or a smaller file.";
            default: return "The conversion failed unexpectedly. Details were saved to the log.";
        }
    }
}
