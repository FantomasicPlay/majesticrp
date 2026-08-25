using System.Collections.Generic;
using MajesticParser.Models;

namespace MajesticParser.Services;

// Перенос блока "БАЗОВЫЕ НАСТРОЙКИ" и связанных констант из parserv2.py
public static class AppConstants
{
    // Пресеты форумов (одинаковая структура XenForo). Первый — форум по умолчанию.
    // Объявлен ДО BaseUrl: статические поля инициализируются по порядку.
    public static readonly List<Forum> Forums = new()
    {
        new Forum { Name = "Majestic RP", BaseUrl = "https://forum.majestic-rp.ru" },
        new Forum { Name = "Россия Онлайн", BaseUrl = "https://forum.russia.online" },
    };

    // Активный форум. Меняется в рантайме при переключении форума в интерфейсе.
    // По умолчанию — Majestic RP (совместимо со старыми данными в %LOCALAPPDATA%\MajesticParser).
    public static string BaseUrl = Forums[0].BaseUrl;

    public const int Delay = 1; // секунды между запросами (было 2 — снижено для скорости)
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
        "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    public const int NavRetries = 2;
    // Картинки качаются чистым HTTP (мимо Cloudflare-браузера) — можно много потоков.
    public const int ImageDownloadWorkers = 16; // было 6
    public const int MaxForumPages = 500;
    public const int MaxThreadPages = 1000;

    // ===== Имена файлов =====
    public const int FilenameMaxLen = 170;
    public const bool FilenameAddId = true;
    public const bool FilenameUnderscore = true;
    public const bool FilenameSmartCut = true;
    // static readonly (а не const), чтобы менять флаг без warning'ов про unreachable code
    public static readonly bool FilenameRemoveStopwords = false;

    public static readonly string[] StopWords =
    {
        "законопроект", "официальный", "текст", "версия",
        "штата", "сан-андриас", "закон", "кодекс"
    };

    // ===== Сохранение =====
    public const string CombinedFilename = "ALL_MAJESTIC_LAWS.txt";

    // ===== Изображения =====
    public const string ImageDirName = "images";
    public const int ImageTimeout = 30;   // секунды
    public const int ImageRetries = 2;
    public static readonly bool ImageRemoveSizeParams = true;
    // auto / data-url / data-src / src
    public const string ImagePreferredSource = "auto";

    public static readonly List<string> ImageAllowedDomains = new();
    public static readonly List<string> ImageBlockedDomains = new();

    public const bool OnlyUserContentImages = true;
    public const bool StrictMessageContentScope = true;

    public static readonly string[] SkipImageClassParts =
    {
        "avatar", "logo", "smilie", "emoji", "icon", "sprite", "reaction"
    };

    public static readonly string[] BlockedImagePathParts =
    {
        "/styles/", "/data/assets/logo", "/favicon", "/icons/", "/smilies/", "/avatars/"
    };

    // ===== Источники по умолчанию =====
    // Пусто: работаем через выбор сервера → раздела и пользовательские источники.
    public static readonly List<Source> DefaultSources = new();

    // Куки (если нужны xf_session / xf_user) — заполняются при необходимости
    public static readonly Dictionary<string, string> Cookies = new();
}
