using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using MajesticParser.Models;
using OpenQA.Selenium;

namespace MajesticParser.Services;

// Перенос: build_thread_page_url, fetch_all_thread_pages, parse_thread
public class ThreadParser
{
    private static readonly Regex TitleSuffix = new(@"\s*\|.*$", RegexOptions.Singleline);

    private readonly BrowserService _browser;
    private readonly ImageService _images;
    private readonly ParseSettings _settings;
    private readonly string _outputDir;
    private readonly Action<string> _log;
    private readonly CancellationToken _ct;

    public ThreadParser(BrowserService browser, ImageService images, ParseSettings settings,
        string outputDir, Action<string> log, CancellationToken ct)
    {
        _browser = browser;
        _images = images;
        _settings = settings;
        _outputDir = outputDir;
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

    private static string BuildThreadPageUrl(string threadUrl, int pageNum)
        => pageNum == 1 ? threadUrl : $"{threadUrl.TrimEnd('/')}/page-{pageNum}";

    private static IReadOnlyList<IElement> FindMessages(IHtmlDocument doc)
    {
        var messages = doc.QuerySelectorAll("article[data-author]");
        if (messages.Length > 0)
            return messages;
        return doc.QuerySelectorAll("div.message");
    }

    private List<IHtmlDocument> FetchAllPages(string threadUrl)
    {
        var pages = new List<IHtmlDocument>();
        var pageNum = 1;

        while (true)
        {
            _ct.ThrowIfCancellationRequested();

            if (pageNum > AppConstants.MaxThreadPages)
            {
                _log($"  ⚠ Достигнут предел в {AppConstants.MaxThreadPages} страниц темы, останавливаюсь");
                break;
            }

            var pageUrl = BuildThreadPageUrl(threadUrl, pageNum);
            _log($"  📄 Загрузка страницы темы {pageNum}: {pageUrl}");

            if (!_browser.SafeGet(pageUrl))
                break;
            _browser.WaitPageLoaded();

            // ждём появления article (до ~8с), иначе небольшая пауза
            var appeared = false;
            for (var i = 0; i < 40; i++)
            {
                _ct.ThrowIfCancellationRequested();
                if (_browser.Driver.FindElements(By.TagName("article")).Count > 0)
                {
                    appeared = true;
                    break;
                }
                Thread.Sleep(200);
            }
            if (!appeared)
                Sleep(2);

            var doc = HtmlHelper.Parse(_browser.Driver.PageSource);
            var messages = FindMessages(doc);

            if (messages.Count == 0)
            {
                if (pageNum == 1)
                    pages.Add(doc);
                break;
            }

            pages.Add(doc);

            if (!_browser.HasNextPage())
                break;

            pageNum++;
            Sleep(AppConstants.Delay);
        }

        return pages;
    }

    public async Task<ParseResult?> ParseThreadAsync(string url)
    {
        _log($"\n📄 Открываю тему: {url}");

        var pages = FetchAllPages(url);
        if (pages.Count == 0)
        {
            _log("  ⚠ Не удалось получить страницы темы");
            return null;
        }

        var firstDoc = pages[0];
        var titleTag = firstDoc.QuerySelector("h1.p-title-value")
                       ?? (IElement?)firstDoc.QuerySelector("title");
        var threadTitle = titleTag != null ? HtmlHelper.GetSeparatedText(titleTag, " ") : "untitled";
        threadTitle = TitleSuffix.Replace(threadTitle, "").Trim();

        var threadId = UrlHelper.ExtractIdFromUrl(url);
        var filename = FilenameHelper.MakeFilename(threadTitle, threadId);

        _images.SyncFromBrowser(_browser.Driver, url);

        var textParts = new List<string>();
        var combinedImages = new List<DownloadedImage>();
        var totalMessages = 0;

        for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            var doc = pages[pageIndex];
            var messages = FindMessages(doc);
            _log($"  Сообщений найдено на странице {pageIndex + 1}: {messages.Count}");

            foreach (var msg in messages)
            {
                _ct.ThrowIfCancellationRequested();
                totalMessages++;

                var authorEl = msg.QuerySelector("a.username") ?? msg.QuerySelector("span.username");
                var authorName = authorEl != null ? authorEl.TextContent.Trim() : "Unknown";

                var postId = MessageParser.ExtractPostId(msg);

                var downloadedImages = new List<DownloadedImage>();
                if (_settings.SaveImages)
                {
                    downloadedImages = await _images.DownloadImagesFromMessageAsync(
                        msg, threadId, authorName, _ct);
                    combinedImages.AddRange(downloadedImages);
                }

                var msgText = "";
                if (_settings.ParseText)
                    msgText = MessageParser.ExtractText(msg);

                // режим: только изображения — текст не собираем
                if (_settings.DownloadOnlyImages)
                    continue;

                if (!string.IsNullOrEmpty(msgText) || downloadedImages.Count > 0)
                {
                    var block = new StringBuilder();
                    block.Append('\n').Append(new string('=', 80)).Append('\n');
                    block.Append($"[Сообщение #{totalMessages}] Автор: {authorName} | Post ID: {postId} | Страница темы: {pageIndex + 1}\n");
                    block.Append(new string('=', 80)).Append('\n');

                    if (!string.IsNullOrEmpty(msgText))
                        block.Append(msgText).Append('\n');

                    if (downloadedImages.Count > 0)
                    {
                        block.Append("\n[Изображения]\n");
                        foreach (var img in downloadedImages)
                            block.Append($"- {img.File}\n");
                    }

                    textParts.Add(block.ToString());
                }
            }
        }

        // режим: только изображения
        if (_settings.DownloadOnlyImages)
        {
            if (_settings.SaveImages && combinedImages.Count > 0)
            {
                _images.SaveManifest(threadId, combinedImages);
                _log($"  ✓ Текст не парсился. Скачано изображений: {combinedImages.Count}");
                return new ParseResult
                {
                    Id = threadId, Title = threadTitle, File = null, Images = combinedImages.Count
                };
            }

            _log("  ⚠ В режиме только изображений ничего не найдено");
            return null;
        }

        if (textParts.Count == 0)
        {
            _log("  ⚠ В теме не удалось извлечь ни текст, ни изображения");
            return null;
        }

        var finalText =
            $"Тема: {threadTitle}\n" +
            $"ID: {threadId}\n" +
            $"URL: {url}\n" +
            new string('=', 100) + "\n" +
            string.Concat(textParts);

        if (_settings.SaveEachThreadToFile)
        {
            OutputWriter.SaveThreadFile(_outputDir, filename, finalText);
            _log($"  ✓ Сохранено: {filename}");
        }

        if (_settings.SaveImages && combinedImages.Count > 0)
            _images.SaveManifest(threadId, combinedImages);

        return new ParseResult
        {
            Id = threadId, Title = threadTitle, File = filename, Images = combinedImages.Count
        };
    }
}
