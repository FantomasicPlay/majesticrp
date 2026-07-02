using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MajesticParser.Models;

namespace MajesticParser.Services;

// Перенос блоков JSON/CONFIG, источников и кэша тредов.
// Конфиг и кэш хранятся в %LOCALAPPDATA%\MajesticParser\
public class ConfigService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string DataDir { get; }
    public string ConfigPath { get; }
    public string ThreadCachePath { get; }
    public string ServersCachePath { get; }

    public ConfigService()
    {
        DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MajesticParser");
        Directory.CreateDirectory(DataDir);

        ConfigPath = Path.Combine(DataDir, "parser_config.json");
        ThreadCachePath = Path.Combine(DataDir, "thread_cache.json");
        ServersCachePath = Path.Combine(DataDir, "servers_cache.json");
        NodeCachePath = Path.Combine(DataDir, "node_cache.json");
    }

    public string NodeCachePath { get; }

    // ===== низкоуровневая работа с JSON =====

    private static T LoadJson<T>(string path, T fallback)
    {
        // Основной файл, при повреждении — резервная копия .bak
        foreach (var p in new[] { path, path + ".bak" })
        {
            if (!File.Exists(p))
                continue;
            try
            {
                var json = File.ReadAllText(p);
                var data = JsonSerializer.Deserialize<T>(json);
                if (data != null)
                    return data;
            }
            catch
            {
                // пробуем следующий (резервную копию)
            }
        }
        return fallback;
    }

    // Атомарная запись: пишем во временный файл, бэкапим старый, затем заменяем.
    // Даже при сбое во время записи данные не теряются.
    private static void SaveJson<T>(string path, T data)
    {
        var json = JsonSerializer.Serialize(data, JsonOpts);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);

        if (File.Exists(path))
        {
            // File.Replace делает резервную копию старого файла в .bak
            File.Replace(tmp, path, path + ".bak");
        }
        else
        {
            File.Move(tmp, path);
        }
    }

    // ===== конфиг =====

    public AppConfig LoadConfig()
    {
        var cfg = LoadJson(ConfigPath, new AppConfig());
        if (string.IsNullOrEmpty(cfg.BaseOutputDir))
            cfg.BaseOutputDir = Directory.GetCurrentDirectory();
        cfg.CustomSources ??= new List<Source>();
        return cfg;
    }

    public void SaveConfig(AppConfig cfg) => SaveJson(ConfigPath, cfg);

    // ===== кэш тредов =====

    public Dictionary<string, ForumCacheEntry> LoadThreadCache()
        => LoadJson(ThreadCachePath, new Dictionary<string, ForumCacheEntry>());

    public void SaveThreadCache(Dictionary<string, ForumCacheEntry> cache)
        => SaveJson(ThreadCachePath, cache);

    // ===== кэш серверов (чтобы не загружать каждый запуск) =====

    public List<ServerCategory> LoadServers()
        => LoadJson(ServersCachePath, new List<ServerCategory>());

    public void SaveServers(List<ServerCategory> servers)
        => SaveJson(ServersCachePath, servers);

    // ===== кэш содержимого узлов дерева (подфорумы + темы) =====

    public Dictionary<string, NodeCacheEntry> LoadNodeCache()
        => LoadJson(NodeCachePath, new Dictionary<string, NodeCacheEntry>());

    public void SaveNodeCache(Dictionary<string, NodeCacheEntry> cache)
        => SaveJson(NodeCachePath, cache);

    // ===== источники =====

    // DEFAULT_SOURCES + custom, без дублей по url
    public List<Source> GetAllSources(AppConfig cfg)
    {
        var result = new List<Source>();
        var seen = new HashSet<string>();

        foreach (var src in AppConstants.DefaultSources.Concat(cfg.CustomSources))
        {
            var url = src.Url.Trim();
            if (seen.Add(url))
                result.Add(src);
        }

        return result;
    }

    public bool IsDefaultSource(Source src)
        => AppConstants.DefaultSources.Any(d => d.Url.Trim() == src.Url.Trim());

    public static string DetectSourceType(string url)
        => url.ToLowerInvariant().Contains("/threads/") ? "thread" : "forum";

    // Возвращает (успех, сообщение)
    public (bool ok, string message) AddSource(AppConfig cfg, string url, string name)
    {
        url = url.Trim();
        if (string.IsNullOrEmpty(url))
            return (false, "URL пустой");

        if (string.IsNullOrWhiteSpace(name))
            name = $"Источник {cfg.CustomSources.Count + 1}";

        var type = DetectSourceType(url);

        if (GetAllSources(cfg).Any(s => s.Url.Trim() == url))
            return (false, "Такой источник уже существует");

        cfg.CustomSources.Add(new Source { Name = name.Trim(), Url = url, Type = type });
        SaveConfig(cfg);
        return (true, $"Источник добавлен: [{type}] {name}");
    }

    public (bool ok, string message) RemoveCustomSource(AppConfig cfg, Source src)
    {
        var match = cfg.CustomSources.FirstOrDefault(s => s.Url.Trim() == src.Url.Trim());
        if (match == null)
            return (false, "Источник не найден среди пользовательских");

        cfg.CustomSources.Remove(match);
        SaveConfig(cfg);
        return (true, $"Источник удалён: {match.Name}");
    }
}
