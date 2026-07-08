using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using AngleSharp.Dom;
using MajesticParser.Models;

namespace MajesticParser.Services;

// Перенос: fetch_forum_threads_with_titles, make_thread_entry, fetch_child_forums,
// sync_forum_cache, gather_threads_from_forum_scope, get_threads_from_cache_for_source,
// unique_thread_list
public class ForumScraper
{
    private static readonly Regex ThreadHrefStrict = new(@"^/threads/.+\.\d+/?$", RegexOptions.Compiled);
    private static readonly Regex ThreadHrefLoose = new(@"/threads/.+\.\d+/?$", RegexOptions.Compiled);
    // Раздел форума: /forums/... или /categories/... (с числовым id или без).
    // НЕ матчит /link-forums/ (внешние ссылки) и /threads/.
    private static readonly Regex ForumHref = new(@"^/(forums|categories)/[^/?#]+/?$", RegexOptions.Compiled);
    private static readonly Regex MultiSpace = new(@"\s+", RegexOptions.Compiled);

    private static readonly string[] NavClassParts =
        { "p-breadcrumbs", "p-nav", "p-sectionlinks", "pagenav" };

    private readonly BrowserService _browser;
    private readonly Action<string> _log;
    private readonly CancellationToken _ct;

    public ForumScraper(BrowserService browser, Action<string> log, CancellationToken ct)
    {
        _browser = browser;
        _log = log;
        _ct = ct;
    }

    private void Sleep(int seconds)
    {
        for (var i = 0; i < seconds * 10; i++)
        {
            _ct.ThrowIfCancellationRequested();
            Thread.Sleep(100);
        }
    }

    // ===== список тредов форума =====

    private static ThreadInfo? MakeThreadEntry(IElement link, HashSet<string> seen)
    {
        var href = (link.GetAttribute("href") ?? "").Trim();
        if (string.IsNullOrEmpty(href) || href.Contains("/unread") || href.Contains("/latest"))
            return null;

        var fullUrl = UrlHelper.NormalizeUrl(href);
        if (!seen.Add(fullUrl))
            return null;

        return new ThreadInfo
        {
            Title = HtmlHelper.GetSeparatedText(link, " "),
            Url = fullUrl,
            Id = UrlHelper.ExtractIdFromUrl(fullUrl),
            Missing = false
        };
    }

    public List<ThreadInfo> FetchForumThreads(string forumUrl)
    {
        var threads = new List<ThreadInfo>();
        var seen = new HashSet<string>();
        var page = 1;

        while (true)
        {
            _ct.ThrowIfCancellationRequested();

            if (page > AppConstants.MaxForumPages)
            {
                _log($"  ⚠ Достигнут предел в {AppConstants.MaxForumPages} страниц, останавливаюсь");
                break;
            }

            var pageUrl = page == 1 ? forumUrl : $"{forumUrl}?page={page}";
            _log($"\n📑 Синхронизация форума, страница {page}: {pageUrl}");

            if (!_browser.SafeGet(pageUrl))
                break;
            _browser.WaitPageLoaded();
            Sleep(AppConstants.Delay);

            var doc = HtmlHelper.Parse(_browser.Driver.PageSource);
            var foundThisPage = 0;

            foreach (var item in doc.QuerySelectorAll("div.structItem"))
            {
                var link = item.QuerySelectorAll("a[href]")
                    .FirstOrDefault(a => ThreadHrefStrict.IsMatch(a.GetAttribute("href") ?? ""));
                if (link == null)
                    continue;

                var entry = MakeThreadEntry(link, seen);
                if (entry != null)
                {
                    threads.Add(entry);
                    foundThisPage++;
                }
            }

            if (foundThisPage == 0)
            {
                foreach (var link in doc.QuerySelectorAll("a[href]")
                             .Where(a => ThreadHrefLoose.IsMatch(a.GetAttribute("href") ?? "")))
                {
                    var entry = MakeThreadEntry(link, seen);
                    if (entry != null)
                    {
                        threads.Add(entry);
                        foundThisPage++;
                    }
                }
            }

            if (foundThisPage == 0)
            {
                _log("  ⚠ Новых записей на странице не найдено");
                break;
            }

            if (!_browser.HasNextPage())
            {
                _log("  ⏹ Последняя страница форума");
                break;
            }

            page++;
            Sleep(AppConstants.Delay);
        }

        return threads;
    }

    // ===== главная форума: категории-серверы и их разделы =====

    public List<ServerCategory> FetchIndexCategories(string indexUrl)
    {
        _log($"\n🖥 Читаю главную форума: {indexUrl}");

        if (!_browser.SafeGet(indexUrl))
            return new List<ServerCategory>();
        _browser.WaitPageLoaded();
        Sleep(AppConstants.Delay);

        var doc = HtmlHelper.Parse(_browser.Driver.PageSource);

        // Имя сервера -> категория (с дедупликацией: тема оборачивает блок дважды)
        var byName = new Dictionary<string, ServerCategory>();
        var order = new List<string>();

        // XenForo: каждая категория-сервер — блок .block--category с заголовком и узлами внутри
        var blocks = doc.All.Where(e => e.ClassList.Contains("block--category")).ToList();

        foreach (var block in blocks)
        {
            var headerEl = block.QuerySelector(".block-header");
            var name = headerEl != null
                ? MultiSpace.Replace(HtmlHelper.GetSeparatedText(headerEl, " "), " ").Trim()
                : "";
            if (string.IsNullOrEmpty(name))
                continue;

            var sections = new List<ForumNode>();
            var seen = new HashSet<string>();

            // Разделы = ссылки .node-title (форумы и категории, кроме внешних /link-forums/)
            foreach (var link in block.QuerySelectorAll(".node-title a[href]"))
            {
                var href = (link.GetAttribute("href") ?? "").Trim();
                if (!ForumHref.IsMatch(href))
                    continue;

                var fullUrl = UrlHelper.NormalizeUrl(href).TrimEnd('/') + "/";
                var key = UrlHelper.NormalizeForCompare(fullUrl);
                if (!seen.Add(key))
                    continue;

                var title = MultiSpace.Replace(HtmlHelper.GetSeparatedText(link, " "), " ").Trim();
                if (string.IsNullOrEmpty(title))
                    continue;

                sections.Add(new ForumNode
                {
                    Name = title,
                    Url = fullUrl,
                    Id = UrlHelper.ExtractIdFromUrl(fullUrl),
                    Type = "forum"
                });
            }

            if (sections.Count == 0)
                continue;

            // Дедупликация по имени: оставляем вариант с бОльшим числом разделов
            if (!byName.TryGetValue(name, out var existing))
            {
                byName[name] = new ServerCategory { Name = name, Sections = sections };
                order.Add(name);
            }
            else if (sections.Count > existing.Sections.Count)
            {
                existing.Sections = sections;
            }
        }

        var categories = order.Select(n => byName[n]).ToList();
        _log($"  ✓ Найдено серверов/категорий: {categories.Count}");
        return categories;
    }

    // ===== подфорумы =====

    public List<ForumNode> FetchChildForums(string forumUrl)
    {
        _log($"\n📁 Проверяю подфорумы: {forumUrl}");

        if (!_browser.SafeGet(forumUrl))
            return new List<ForumNode>();
        _browser.WaitPageLoaded();
        Sleep(AppConstants.Delay);

        var doc = HtmlHelper.Parse(_browser.Driver.PageSource);
        var currentUrl = UrlHelper.NormalizeForCompare(forumUrl);
        var results = new List<ForumNode>();
        var seen = new HashSet<string>();

        void AddForumLink(IElement link)
        {
            var href = (link.GetAttribute("href") ?? "").Trim();
            if (string.IsNullOrEmpty(href) || !ForumHref.IsMatch(href))
                return;

            var fullUrl = UrlHelper.NormalizeUrl(href).TrimEnd('/') + "/";
            var compareUrl = UrlHelper.NormalizeForCompare(fullUrl);

            if (compareUrl == currentUrl || !seen.Add(compareUrl))
                return;

            var title = MultiSpace.Replace(HtmlHelper.GetSeparatedText(link, " "), " ").Trim();
            if (string.IsNullOrEmpty(title))
            {
                seen.Remove(compareUrl); // не считаем пустые
                return;
            }

            results.Add(new ForumNode
            {
                Name = title,
                Url = fullUrl,
                Id = UrlHelper.ExtractIdFromUrl(fullUrl),
                Type = "forum"
            });
        }

        // Основной способ XenForo: node-блоки
        var nodes = doc.All.Where(e =>
            e.ClassList.Any(c => c == "node" || c.StartsWith("node--")));
        foreach (var node in nodes)
        {
            var link = node.QuerySelectorAll("a[href]")
                .FirstOrDefault(a => ForumHref.IsMatch(a.GetAttribute("href") ?? ""));
            if (link != null)
                AddForumLink(link);
        }

        // Fallback: все ссылки на форумы, отсекая навигацию/хлебные крошки
        if (results.Count == 0)
        {
            foreach (var link in doc.QuerySelectorAll("a[href]")
                         .Where(a => ForumHref.IsMatch(a.GetAttribute("href") ?? "")))
            {
                if (HasNavAncestor(link))
                    continue;
                AddForumLink(link);
            }
        }

        if (results.Count > 0)
            _log($"  ✓ Найдено подфорумов: {results.Count}");
        else
            _log("  ⏹ Подфорумы не найдены — это конечный форум или форум без подразделов");

        return results;
    }

    private static bool HasNavAncestor(IElement el)
    {
        var current = el.ParentElement;
        while (current != null)
        {
            var classes = (current.GetAttribute("class") ?? "").ToLowerInvariant();
            if (NavClassParts.Any(part => classes.Contains(part)))
                return true;
            current = current.ParentElement;
        }
        return false;
    }

    // ===== синхронизация кэша =====

    public void SyncForumCache(Source source, Dictionary<string, ForumCacheEntry> cache)
    {
        if (source.Type != "forum")
            return;

        var forumUrl = source.Url.Trim();
        _log($"\n🔄 Синхронизация источника: {source.Name}");
        _log($"URL: {forumUrl}");

        var actualThreads = FetchForumThreads(forumUrl);

        if (!cache.ContainsKey(forumUrl))
            cache[forumUrl] = new ForumCacheEntry { SourceName = source.Name };

        var cachedThreads = cache[forumUrl].Threads;
        var actualMap = actualThreads.GroupBy(t => t.Url).ToDictionary(g => g.Key, g => g.First());
        var cachedMap = cachedThreads.GroupBy(t => t.Url).ToDictionary(g => g.Key, g => g.First());

        int newCount = 0, updatedCount = 0, missingCount = 0;
        var merged = new List<ThreadInfo>();

        foreach (var (url, actual) in actualMap)
        {
            if (!cachedMap.TryGetValue(url, out var cached))
            {
                merged.Add(actual);
                newCount++;
            }
            else
            {
                if (cached.Title != actual.Title)
                    updatedCount++;

                merged.Add(new ThreadInfo
                {
                    Title = actual.Title, Url = actual.Url, Id = actual.Id, Missing = false
                });
            }
        }

        foreach (var (url, cached) in cachedMap)
        {
            if (!actualMap.ContainsKey(url))
            {
                merged.Add(new ThreadInfo
                {
                    Title = cached.Title, Url = cached.Url, Id = cached.Id, Missing = true
                });
                missingCount++;
            }
        }

        merged = merged.OrderBy(t => int.TryParse(t.Id, out var n) ? n : 0).ToList();

        cache[forumUrl] = new ForumCacheEntry
        {
            SourceName = source.Name,
            LastSync = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Threads = merged
        };

        _log($"  ✓ Новых тредов: {newCount}");
        _log($"  ✓ Обновлённых названий: {updatedCount}");
        _log($"  ✓ Пропавших/недоступных: {missingCount}");
    }

    public List<ThreadInfo> GetThreadsForSource(Source source,
        Dictionary<string, ForumCacheEntry> cache, bool includeMissing = false)
    {
        if (source.Type == "thread")
        {
            var url = source.Url.Trim();
            return new List<ThreadInfo>
            {
                new() { Title = source.Name, Url = url, Id = UrlHelper.ExtractIdFromUrl(url), Missing = false }
            };
        }

        var forumUrl = source.Url.Trim();
        if (!cache.TryGetValue(forumUrl, out var entry))
            return new List<ThreadInfo>();

        return includeMissing
            ? entry.Threads
            : entry.Threads.Where(t => !t.Missing).ToList();
    }

    // ===== рекурсивный сбор тредов =====

    public List<ThreadInfo> GatherThreadsFromScope(Source source,
        Dictionary<string, ForumCacheEntry> cache, bool includeSubforums,
        HashSet<string>? visited = null, HashSet<string>? hidden = null)
    {
        visited ??= new HashSet<string>();
        hidden ??= new HashSet<string>();

        var forumUrl = source.Url.Trim();
        var key = UrlHelper.NormalizeForCompare(forumUrl);
        if (!visited.Add(key))
            return new List<ThreadInfo>();

        // Пропускаем удалённые пользователем разделы
        if (hidden.Contains(key))
            return new List<ThreadInfo>();

        SyncForumCache(source, cache);
        var threads = GetThreadsForSource(source, cache, includeMissing: false)
            // и удалённые темы
            .Where(t => !hidden.Contains(UrlHelper.NormalizeForCompare(t.Url)))
            .ToList();

        if (includeSubforums)
        {
            foreach (var child in FetchChildForums(forumUrl))
            {
                if (hidden.Contains(UrlHelper.NormalizeForCompare(child.Url)))
                    continue; // удалённый подфорум не обходим
                var childSource = new Source { Name = child.Name, Url = child.Url, Type = "forum" };
                threads.AddRange(GatherThreadsFromScope(childSource, cache, true, visited, hidden));
            }
        }

        return threads;
    }

    public static List<ThreadInfo> UniqueThreads(IEnumerable<ThreadInfo> threads)
    {
        var result = new List<ThreadInfo>();
        var seen = new HashSet<string>();
        foreach (var t in threads)
        {
            var key = UrlHelper.NormalizeForCompare(t.Url.Trim());
            if (string.IsNullOrEmpty(key) || !seen.Add(key))
                continue;
            result.Add(t);
        }
        return result;
    }
}
