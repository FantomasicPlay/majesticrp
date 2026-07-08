using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MajesticParser.Models;

namespace MajesticParser.Services;

// Координатор жизненного цикла Selenium и прохода по тредам.
// Соответствует логике run_parsing_session, но без консольного ввода —
// выбор источников/тредов делает GUI.
public class ParserEngine : IDisposable
{
    private readonly Action<string> _log;
    private BrowserService? _browser;
    private bool _browserHeadless;

    public ParserEngine(Action<string> log)
    {
        _log = log;
    }

    public BrowserService EnsureBrowser(bool headless)
    {
        if (_browser != null && _browserHeadless == headless)
            return _browser;

        _browser?.Dispose();
        _log(headless ? "🌐 Запускаю браузер (headless)..." : "🌐 Запускаю браузер...");
        _browser = new BrowserService(headless, _log);
        _browserHeadless = headless;
        _browser.ApplyCookiesIfNeeded();
        return _browser;
    }

    // ===== загрузка тредов для источника (для дерева выбора в GUI) =====

    public List<ThreadInfo> LoadThreadsForSource(Source source,
        Dictionary<string, ForumCacheEntry> cache, bool includeSubforums,
        bool headless, CancellationToken ct)
    {
        var browser = EnsureBrowser(headless);
        var scraper = new ForumScraper(browser, _log, ct);

        if (source.Type == "thread")
            return scraper.GetThreadsForSource(source, cache);

        var threads = includeSubforums
            ? scraper.GatherThreadsFromScope(source, cache, includeSubforums: true)
            : RunSingleForum(scraper, source, cache);

        return ForumScraper.UniqueThreads(threads);
    }

    private static List<ThreadInfo> RunSingleForum(ForumScraper scraper, Source source,
        Dictionary<string, ForumCacheEntry> cache)
    {
        scraper.SyncForumCache(source, cache);
        return scraper.GetThreadsForSource(source, cache, includeMissing: false);
    }

    public List<ForumNode> LoadChildForums(string forumUrl, bool headless, CancellationToken ct)
    {
        var browser = EnsureBrowser(headless);
        var scraper = new ForumScraper(browser, _log, ct);
        return scraper.FetchChildForums(forumUrl);
    }

    // Категории-серверы с главной форума (+ их разделы)
    public List<ServerCategory> LoadServerCategories(bool headless, CancellationToken ct)
    {
        var browser = EnsureBrowser(headless);
        var scraper = new ForumScraper(browser, _log, ct);
        return scraper.FetchIndexCategories(AppConstants.BaseUrl + "/");
    }

    // Загрузка одного узла дерева: подфорумы этого форума + его собственные темы.
    public (List<ForumNode> subforums, List<ThreadInfo> threads) LoadForumNode(
        Source source, Dictionary<string, ForumCacheEntry> cache, bool headless, CancellationToken ct)
    {
        var browser = EnsureBrowser(headless);
        var scraper = new ForumScraper(browser, _log, ct);

        var subforums = scraper.FetchChildForums(source.Url.Trim());
        scraper.SyncForumCache(source, cache);
        var threads = scraper.GetThreadsForSource(source, cache, includeMissing: false);

        return (subforums, threads);
    }

    // Все темы форума рекурсивно (для выбранной галочкой папки-раздела).
    // hidden — нормализованные URL удалённых пользователем узлов (пропускаются).
    public List<ThreadInfo> GatherForumThreads(Source source,
        Dictionary<string, ForumCacheEntry> cache, bool headless, CancellationToken ct,
        HashSet<string>? hidden = null)
    {
        var browser = EnsureBrowser(headless);
        var scraper = new ForumScraper(browser, _log, ct);
        return ForumScraper.UniqueThreads(
            scraper.GatherThreadsFromScope(source, cache, includeSubforums: true, hidden: hidden));
    }

    // ===== основной прогон =====

    public async Task<(int parsed, int images, bool done)> RunParsingAsync(
        List<ThreadInfo> threads, ParseSettings settings, string outputDir, bool isResume,
        CancellationToken ct)
    {
        var browser = EnsureBrowser(settings.Headless);
        var images = new ImageService(outputDir, settings, _log);
        var parser = new ThreadParser(browser, images, settings, outputDir, _log, ct);

        var progress = isResume ? OutputWriter.LoadProgress(outputDir) : new Dictionary<string, ParseResult>();
        var results = new List<ParseResult>();
        var totalImages = 0;

        _log($"\n📂 Папка текущего запуска: {outputDir}");
        _log($"\n🚀 Начинаю парсинг выбранных конечных тредов: {threads.Count}");

        for (var i = 0; i < threads.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var thread = threads[i];
            var threadId = string.IsNullOrEmpty(thread.Id) ? "0" : thread.Id;

            if (progress.TryGetValue(threadId, out var cached))
            {
                _log($"\n[{i + 1}/{threads.Count}] ⏭ Уже обработан ранее: {thread.Title}");
                results.Add(cached);
                totalImages += cached.Images;
                continue;
            }

            _log($"\n[{i + 1}/{threads.Count}]");
            _log($"Тред: {thread.Title}");
            _log($"URL: {thread.Url}");

            try
            {
                var result = await parser.ParseThreadAsync(thread.Url);
                if (result != null)
                {
                    results.Add(result);
                    totalImages += result.Images;
                    progress[threadId] = result;
                    OutputWriter.SaveProgress(outputDir, progress);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                _log($"  ❌ Ошибка при парсинге треда {thread.Url}: {e.Message}");
            }

            SleepDelay(ct);
        }

        if (settings.CombineIntoOne && !settings.DownloadOnlyImages)
            OutputWriter.SaveCombinedFromDir(outputDir, settings.CombineIntoOne,
                settings.SaveEachThreadToFile, _log);

        var allDone = threads.All(t => progress.ContainsKey(string.IsNullOrEmpty(t.Id) ? "0" : t.Id));
        if (allDone)
            OutputWriter.MarkRunDone(outputDir);
        else
            _log("⚠ Не все треды обработаны успешно — запуск останется доступным для возобновления");

        return (results.Count, totalImages, allDone);
    }

    private static void SleepDelay(CancellationToken ct)
    {
        for (var i = 0; i < AppConstants.Delay * 10; i++)
        {
            ct.ThrowIfCancellationRequested();
            Thread.Sleep(100);
        }
    }

    public void Dispose()
    {
        _browser?.Dispose();
        _browser = null;
    }
}
