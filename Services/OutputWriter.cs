using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MajesticParser.Models;

namespace MajesticParser.Services;

// Перенос: build_output_dir, progress/resume хелперы, save thread file,
// save_combined_from_dir, manifest управляется ImageService.
public static class OutputWriter
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private const string RunPrefix = "majestic_laws_";

    private static readonly Regex TitleLine = new(@"^Тема: (.*)$", RegexOptions.Multiline);
    private static readonly Regex IdLine = new(@"^ID: (.*)$", RegexOptions.Multiline);

    public static string BuildOutputDir(string baseDir)
    {
        var dt = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var dir = Path.Combine(baseDir, $"{RunPrefix}{dt}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ===== resume =====

    public static string ProgressPath(string outputDir) => Path.Combine(outputDir, "progress.json");
    public static string DoneMarkerPath(string outputDir) => Path.Combine(outputDir, "_DONE");

    public static Dictionary<string, ParseResult> LoadProgress(string outputDir)
    {
        var path = ProgressPath(outputDir);
        if (!File.Exists(path))
            return new Dictionary<string, ParseResult>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, ParseResult>>(File.ReadAllText(path))
                   ?? new Dictionary<string, ParseResult>();
        }
        catch
        {
            return new Dictionary<string, ParseResult>();
        }
    }

    public static void SaveProgress(string outputDir, Dictionary<string, ParseResult> progress)
        => File.WriteAllText(ProgressPath(outputDir), JsonSerializer.Serialize(progress, JsonOpts));

    public static void MarkRunDone(string outputDir)
        => File.WriteAllText(DoneMarkerPath(outputDir), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

    public static List<string> FindResumableRuns(string baseDir)
    {
        if (!Directory.Exists(baseDir))
            return new List<string>();

        return Directory.GetDirectories(baseDir)
            .Where(d => Path.GetFileName(d).StartsWith(RunPrefix))
            .Where(d => File.Exists(ProgressPath(d)) && !File.Exists(DoneMarkerPath(d)))
            .OrderByDescending(d => Path.GetFileName(d))
            .ToList();
    }

    // ===== per-thread =====

    public static void SaveThreadFile(string outputDir, string filename, string finalText)
    {
        var path = Path.Combine(outputDir, filename);
        File.WriteAllText(path, finalText, new UTF8Encoding(false));
    }

    // ===== общий файл, собранный с диска =====

    public static string? SaveCombinedFromDir(string outputDir, bool combineIntoOne, bool saveEachThread,
        Action<string> log)
    {
        if (!combineIntoOne)
            return null;

        if (!saveEachThread)
        {
            log("  ⚠ COMBINE_INTO_ONE включён, но per-thread сохранение выключено — общий файл не собрать");
            return null;
        }

        var txtFiles = Directory.GetFiles(outputDir, "*.txt")
            .Select(Path.GetFileName)
            .Where(f => f != null && f != AppConstants.CombinedFilename)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        if (txtFiles.Count == 0)
            return null;

        log($"\n📚 Создаю {AppConstants.CombinedFilename}");

        var items = new List<(string title, string id, string content)>();
        foreach (var fname in txtFiles)
        {
            var text = File.ReadAllText(Path.Combine(outputDir, fname!));
            var titleM = TitleLine.Match(text);
            var idM = IdLine.Match(text);
            items.Add((
                titleM.Success ? titleM.Groups[1].Value.Trim() : fname!,
                idM.Success ? idM.Groups[1].Value.Trim() : "0",
                text));
        }

        items = items.OrderBy(i => int.TryParse(i.id, out var n) ? n : 0).ToList();

        var sb = new StringBuilder();
        var lines = new List<string>
        {
            new string('=', 100),
            "СБОРНИК ТЕМ — MAJESTIC RP",
            $"Дата: {DateTime.Now:dd.MM.yyyy HH:mm:ss}",
            $"Всего тем: {items.Count}",
            new string('=', 100),
            "",
            "ОГЛАВЛЕНИЕ",
            new string('-', 100)
        };

        for (var i = 0; i < items.Count; i++)
            lines.Add($"{i + 1:D3}. [{items[i].id}] {items[i].title}");

        lines.Add("\n" + new string('=', 100) + "\n");

        foreach (var item in items)
        {
            lines.Add(item.content);
            lines.Add("\n" + new string('=', 100) + "\n");
        }

        sb.Append(string.Join("\n", lines));

        var path = Path.Combine(outputDir, AppConstants.CombinedFilename);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

        var sizeKb = new FileInfo(path).Length / 1024.0;
        log($"  ✓ Общий файл сохранён: {path} ({sizeKb:F1} КБ)");
        return path;
    }
}
