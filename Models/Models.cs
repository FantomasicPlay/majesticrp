using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MajesticParser.Models;

// ================= КОНФИГ =================

public class AppConfig
{
    [JsonPropertyName("is_first_run_completed")]
    public bool IsFirstRunCompleted { get; set; } = false;

    [JsonPropertyName("base_output_dir")]
    public string BaseOutputDir { get; set; } = "";

    [JsonPropertyName("custom_sources")]
    public List<Source> CustomSources { get; set; } = new();

    // Последний открытый раздел — чтобы восстановить дерево при следующем запуске
    [JsonPropertyName("last_server_name")]
    public string LastServerName { get; set; } = "";

    [JsonPropertyName("last_section_url")]
    public string LastSectionUrl { get; set; } = "";

    [JsonPropertyName("last_section_name")]
    public string LastSectionName { get; set; } = "";

    [JsonPropertyName("last_section_id")]
    public string LastSectionId { get; set; } = "0";

    // URL узлов, удалённых пользователем из дерева (скрываются навсегда)
    [JsonPropertyName("hidden_urls")]
    public List<string> HiddenUrls { get; set; } = new();
}

// Снимок содержимого форума/раздела для мгновенного построения дерева из кэша
public class NodeCacheEntry
{
    [JsonPropertyName("subforums")]
    public List<ForumNode> Subforums { get; set; } = new();

    [JsonPropertyName("threads")]
    public List<ThreadInfo> Threads { get; set; } = new();

    [JsonPropertyName("last_sync")]
    public string LastSync { get; set; } = "";
}

// Источник: раздел форума или прямой тред
public class Source
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    // "forum" | "thread"
    [JsonPropertyName("type")]
    public string Type { get; set; } = "forum";
}

// ================= ТРЕДЫ / КЭШ =================

public class ThreadInfo
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("id")]
    public string Id { get; set; } = "0";

    [JsonPropertyName("missing")]
    public bool Missing { get; set; } = false;
}

public class ForumCacheEntry
{
    [JsonPropertyName("source_name")]
    public string SourceName { get; set; } = "";

    [JsonPropertyName("last_sync")]
    public string LastSync { get; set; } = "";

    [JsonPropertyName("threads")]
    public List<ThreadInfo> Threads { get; set; } = new();
}

// ================= ПОДФОРУМЫ =================

public class ForumNode
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Id { get; set; } = "0";
    public string Type { get; set; } = "forum";
}

// Категория верхнего уровня на главной форума = «сервер»
// (например «Majestic РП | New York»), внутри — разделы (Организации, Жалобы…).
public class ServerCategory
{
    public string Name { get; set; } = "";
    public List<ForumNode> Sections { get; set; } = new();

    public override string ToString() => Name;
}

// ================= ИЗОБРАЖЕНИЯ =================

public class ImageCandidate
{
    public int Index { get; set; }
    public string DirectUrl { get; set; } = "";
    public string ProxyUrl { get; set; } = "";
    public string SrcUrl { get; set; } = "";
    public string Alt { get; set; } = "";
}

public class DownloadedImage
{
    [JsonPropertyName("post_id")]
    public string PostId { get; set; } = "";

    [JsonPropertyName("author")]
    public string Author { get; set; } = "";

    [JsonPropertyName("file")]
    public string File { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("content_type")]
    public string ContentType { get; set; } = "";

    [JsonPropertyName("alt")]
    public string Alt { get; set; } = "";
}

// ================= РЕЗУЛЬТАТ =================

public class ParseResult
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "0";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("file")]
    public string? File { get; set; }

    [JsonPropertyName("images")]
    public int Images { get; set; }
}

// ================= НАСТРОЙКИ ОБРАБОТКИ =================

public enum ImageDownloadMode
{
    None,
    All,
    SelectedPosts,
    SelectedAuthors
}

public class ParseSettings
{
    // Режим обработки
    public bool SaveImages { get; set; } = true;
    public bool ParseText { get; set; } = true;
    public bool DownloadOnlyImages { get; set; } = false;
    public ImageDownloadMode ImageMode { get; set; } = ImageDownloadMode.All;
    public List<int> SelectedPostIds { get; set; } = new();
    public List<string> SelectedAuthors { get; set; } = new();

    // Браузер
    public bool Headless { get; set; } = true;

    // Сохранение
    public bool SaveEachThreadToFile { get; set; } = true;
    public bool CombineIntoOne { get; set; } = true;
}
