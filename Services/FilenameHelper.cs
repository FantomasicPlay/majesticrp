using System;
using System.IO;
using System.Text.RegularExpressions;

namespace MajesticParser.Services;

// Перенос: smart_truncate, sanitize_filename, make_filename,
// make_image_filename, guess_extension_from_url_or_type
public static class FilenameHelper
{
    private static readonly Regex InvalidChars = new(@"[<>:""/\\|?*\x00-\x1F]", RegexOptions.Compiled);
    private static readonly Regex MultiSpace = new(@"\s+", RegexOptions.Compiled);

    public static string SmartTruncate(string text, int maxLen, string suffix = "...")
    {
        if (text.Length <= maxLen)
            return text;

        var trimmed = text.Substring(0, maxLen - suffix.Length).TrimEnd();
        if (trimmed.Contains(' '))
        {
            var idx = trimmed.LastIndexOf(' ');
            trimmed = trimmed.Substring(0, idx).TrimEnd();
        }

        if (string.IsNullOrEmpty(trimmed))
            trimmed = text.Substring(0, maxLen - suffix.Length).TrimEnd();

        return trimmed + suffix;
    }

    public static string SanitizeFilename(string name)
    {
        name = InvalidChars.Replace(name, "");
        name = MultiSpace.Replace(name, " ").Trim();
        name = name.Trim('.', ' ');
        return name;
    }

    public static string MakeFilename(string title, string threadId)
    {
        var clean = title.Trim();

        if (AppConstants.FilenameRemoveStopwords)
        {
            foreach (var word in AppConstants.StopWords)
            {
                clean = Regex.Replace(clean, $@"\b{Regex.Escape(word)}\b", "",
                    RegexOptions.IgnoreCase);
            }
            clean = MultiSpace.Replace(clean, " ").Trim();
        }

        clean = SanitizeFilename(clean);

        if (string.IsNullOrEmpty(clean))
            clean = $"thread_{threadId}";

        if (AppConstants.FilenameMaxLen > 0 && clean.Length > AppConstants.FilenameMaxLen)
        {
            clean = AppConstants.FilenameSmartCut
                ? SmartTruncate(clean, AppConstants.FilenameMaxLen)
                : clean.Substring(0, AppConstants.FilenameMaxLen);
        }

        if (AppConstants.FilenameUnderscore)
            clean = clean.Replace(" ", "_");

        return AppConstants.FilenameAddId ? $"{clean}_{threadId}.txt" : $"{clean}.txt";
    }

    public static string GuessExtension(string url, string contentType = "")
    {
        string path;
        try { path = new Uri(url).AbsolutePath; }
        catch { path = url; }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (!string.IsNullOrEmpty(ext) && ext.Length <= 6)
            return ext;

        if (!string.IsNullOrEmpty(contentType))
        {
            var mime = contentType.Split(';')[0].Trim().ToLowerInvariant();
            var guessed = MimeToExtension(mime);
            if (guessed != null)
                return guessed;
        }

        return ".jpg";
    }

    private static string? MimeToExtension(string mime) => mime switch
    {
        "image/jpeg" => ".jpg",
        "image/jpg" => ".jpg",
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/bmp" => ".bmp",
        "image/svg+xml" => ".svg",
        "image/tiff" => ".tiff",
        "image/x-icon" => ".ico",
        _ => null
    };

    public static string MakeImageFilename(string threadId, string postId, int index,
        string url, string contentType = "")
    {
        string originalName;
        try { originalName = Path.GetFileName(new Uri(url).AbsolutePath).Trim(); }
        catch { originalName = ""; }

        var ext = GuessExtension(url, contentType);

        var stem = !string.IsNullOrEmpty(originalName)
            ? Path.GetFileNameWithoutExtension(originalName)
            : $"image_{index}";

        stem = SanitizeFilename(stem);
        if (string.IsNullOrEmpty(stem))
            stem = $"image_{index}";

        if (AppConstants.FilenameUnderscore)
            stem = stem.Replace(" ", "_");

        return $"thread_{threadId}_post_{postId}_{index:D2}_{stem}{ext}";
    }
}
