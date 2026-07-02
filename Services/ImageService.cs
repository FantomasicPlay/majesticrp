using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using MajesticParser.Models;
using OpenQA.Selenium.Chrome;

namespace MajesticParser.Services;

// Перенос блока ИЗОБРАЖЕНИЯ: extract_image_candidates, choose_image_url,
// download_binary_file, download_one_candidate (+ resume/dedup),
// download_images_from_message (параллельно), manifest, http-сессия.
public class ImageService
{
    private readonly string _outputDir;
    private readonly ParseSettings _settings;
    private readonly Action<string> _log;
    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public ImageService(string outputDir, ParseSettings settings, Action<string> log)
    {
        _outputDir = outputDir;
        _settings = settings;
        _log = log;

        var handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All
        };

        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(AppConstants.ImageTimeout)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(AppConstants.UserAgent);
        _http.DefaultRequestHeaders.Referrer = new Uri(AppConstants.BaseUrl);

        foreach (var (name, value) in AppConstants.Cookies)
            AddCookie(name, value);
    }

    private void AddCookie(string name, string value)
    {
        try { _cookies.Add(new Uri(AppConstants.BaseUrl), new Cookie(name, value)); }
        catch { /* ignore */ }
    }

    // Аналог transfer_cookies_from_selenium + смена Referer на текущий тред
    public void SyncFromBrowser(ChromeDriver driver, string referer)
    {
        try
        {
            foreach (var c in driver.Manage().Cookies.AllCookies)
                AddCookie(c.Name, c.Value);
        }
        catch { /* ignore */ }

        if (!string.IsNullOrEmpty(referer) && Uri.TryCreate(referer, UriKind.Absolute, out var r))
            _http.DefaultRequestHeaders.Referrer = r;
    }

    // ===== выбор изображений из сообщения =====

    public List<ImageCandidate> ExtractCandidates(IElement message)
    {
        var results = new List<ImageCandidate>();

        var content = HtmlHelper.GetMessageContent(message);
        if (content == null)
            return results;

        // 1) Приоритет: bbImageWrapper-обёртки пользовательских изображений
        var wrappers = content.QuerySelectorAll("div[class*=bbImageWrapper]");
        foreach (var wrapper in wrappers)
        {
            var img = wrapper.QuerySelector("img");
            if (img == null)
                continue;

            if (HasSkippedClass(img))
                continue;

            var directUrl = UrlHelper.NormalizeUrl((img.GetAttribute("data-url") ?? "").Trim());
            var src = UrlHelper.NormalizeUrl((img.GetAttribute("src") ?? "").Trim());
            var proxyUrl = UrlHelper.NormalizeUrl((wrapper.GetAttribute("data-src") ?? "").Trim());

            if (string.IsNullOrEmpty(directUrl) && string.IsNullOrEmpty(src) && string.IsNullOrEmpty(proxyUrl))
                continue;

            results.Add(new ImageCandidate
            {
                Index = results.Count + 1,
                DirectUrl = directUrl,
                ProxyUrl = proxyUrl,
                SrcUrl = src,
                Alt = (img.GetAttribute("alt") ?? "").Trim()
            });
        }

        if (results.Count > 0)
            return results;

        // 2) Fallback: <img> внутри контента поста
        var searchRoot = AppConstants.StrictMessageContentScope ? content : message;

        foreach (var img in searchRoot.QuerySelectorAll("img"))
        {
            if (HasSkippedClass(img))
                continue;

            // Не брать картинки из блока пользователя
            if (img.Closest("[class~=message-user]") != null)
                continue;

            // Не брать картинки из footer/header/nav/aside
            if (img.Closest("footer, nav, header, aside") != null)
                continue;

            if (AppConstants.OnlyUserContentImages)
            {
                var validParent =
                    img.Closest("div.bbWrapper")
                    ?? img.Closest("div.message-content")
                    ?? img.Closest("div.message-body");
                if (validParent == null)
                    continue;
            }

            var imgSrc = UrlHelper.NormalizeUrl((img.GetAttribute("src") ?? "").Trim());
            var dataUrl = UrlHelper.NormalizeUrl((img.GetAttribute("data-url") ?? "").Trim());

            if (string.IsNullOrEmpty(imgSrc) && string.IsNullOrEmpty(dataUrl))
                continue;

            results.Add(new ImageCandidate
            {
                Index = results.Count + 1,
                DirectUrl = dataUrl,
                ProxyUrl = "",
                SrcUrl = imgSrc,
                Alt = (img.GetAttribute("alt") ?? "").Trim()
            });
        }

        return results;
    }

    private static bool HasSkippedClass(IElement img)
    {
        var classes = (img.GetAttribute("class") ?? "").ToLowerInvariant();
        return AppConstants.SkipImageClassParts.Any(part => classes.Contains(part));
    }

    public List<string> ChooseImageUrl(ImageCandidate c)
    {
        string[] order = AppConstants.ImagePreferredSource switch
        {
            "data-url" => new[] { c.DirectUrl, c.ProxyUrl, c.SrcUrl },
            "data-src" => new[] { c.ProxyUrl, c.DirectUrl, c.SrcUrl },
            "src" => new[] { c.SrcUrl, c.DirectUrl, c.ProxyUrl },
            _ => new[] { c.DirectUrl, c.ProxyUrl, c.SrcUrl }
        };

        var normalized = new List<string>();
        foreach (var item in order)
        {
            var n = UrlHelper.NormalizeImageUrl(item);
            if (!string.IsNullOrEmpty(n) && !normalized.Contains(n))
                normalized.Add(n);
        }
        return normalized;
    }

    // ===== выбор: качать ли изображения этого сообщения =====

    public bool ShouldDownloadFor(string author, string postId)
    {
        if (!_settings.SaveImages || _settings.ImageMode == ImageDownloadMode.None)
            return false;

        switch (_settings.ImageMode)
        {
            case ImageDownloadMode.All:
                return true;
            case ImageDownloadMode.SelectedPosts:
                return int.TryParse(postId, out var pid) && _settings.SelectedPostIds.Contains(pid);
            case ImageDownloadMode.SelectedAuthors:
                return _settings.SelectedAuthors
                    .Any(a => a.Trim().ToLowerInvariant() == author.Trim().ToLowerInvariant());
            default:
                return false;
        }
    }

    // ===== пути / resume =====

    public string GetThreadImageDir(string threadId)
    {
        var path = Path.Combine(_outputDir, AppConstants.ImageDirName, $"thread_{threadId}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string? FindExistingImage(string imageDir, string threadId, string postId, int index)
    {
        var pattern = $"thread_{threadId}_post_{postId}_{index:D2}_*";
        var matches = Directory.GetFiles(imageDir, pattern)
            .Where(p => new FileInfo(p).Length > 0)
            .ToList();
        return matches.Count > 0 ? Path.GetFileName(matches[0]) : null;
    }

    // ===== скачивание =====

    private async Task<(bool ok, string? error, string contentType)> DownloadBinaryAsync(
        string url, string savePath, CancellationToken ct)
    {
        string? lastError = null;

        for (var attempt = 0; attempt <= AppConstants.ImageRetries; attempt++)
        {
            try
            {
                using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();

                var contentType = (resp.Content.Headers.ContentType?.ToString() ?? "").ToLowerInvariant();

                await using (var fs = File.Create(savePath))
                    await resp.Content.CopyToAsync(fs, ct);

                return (true, null, contentType);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                lastError = e.Message;
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { throw; }
            }
        }

        return (false, lastError, "");
    }

    private async Task<DownloadedImage?> DownloadOneCandidateAsync(
        string threadId, string postId, string imageDir, ImageCandidate item,
        string author, CancellationToken ct)
    {
        // resume/dedup: если файл уже есть — пропускаем
        var existing = FindExistingImage(imageDir, threadId, postId, item.Index);
        if (existing != null)
        {
            _log($"  ⏭ Уже скачано, пропуск: post={postId} -> {existing}");
            return new DownloadedImage
            {
                PostId = postId, Author = author, File = existing,
                Url = "", ContentType = "", Alt = item.Alt
            };
        }

        var variants = ChooseImageUrl(item);
        if (variants.Count == 0)
            return null;

        string lastError = "";

        foreach (var variantUrl in variants)
        {
            if (!UrlHelper.IsAllowedImageUrl(variantUrl))
                continue;

            var tempName = FilenameHelper.MakeImageFilename(threadId, postId, item.Index, variantUrl);
            var tempPath = Path.Combine(imageDir, tempName);

            var (ok, error, contentType) = await DownloadBinaryAsync(variantUrl, tempPath, ct);

            if (ok)
            {
                var actualName = FilenameHelper.MakeImageFilename(threadId, postId, item.Index, variantUrl, contentType);
                var actualPath = Path.Combine(imageDir, actualName);

                if (!string.Equals(tempPath, actualPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(actualPath))
                        File.Delete(actualPath);
                    File.Move(tempPath, actualPath);
                }

                _log($"  🖼 Скачано изображение: post={postId} -> {actualName}");
                return new DownloadedImage
                {
                    PostId = postId, Author = author, File = actualName,
                    Url = variantUrl, ContentType = contentType, Alt = item.Alt
                };
            }

            lastError = error ?? "";
        }

        _log($"  ⚠ Не удалось скачать изображение post={postId}: {lastError}");
        return null;
    }

    public async Task<List<DownloadedImage>> DownloadImagesFromMessageAsync(
        IElement message, string threadId, string author, CancellationToken ct)
    {
        var postId = MessageParser.ExtractPostId(message);

        if (!ShouldDownloadFor(author, postId))
            return new List<DownloadedImage>();

        var imageDir = GetThreadImageDir(threadId);
        var candidates = ExtractCandidates(message);

        if (candidates.Count == 0)
            return new List<DownloadedImage>();

        if (candidates.Count == 1)
        {
            var single = await DownloadOneCandidateAsync(threadId, postId, imageDir, candidates[0], author, ct);
            return single != null ? new List<DownloadedImage> { single } : new List<DownloadedImage>();
        }

        var downloaded = new ConcurrentBag<DownloadedImage>();
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Min(AppConstants.ImageDownloadWorkers, candidates.Count),
            CancellationToken = ct
        };

        await Parallel.ForEachAsync(candidates, options, async (item, token) =>
        {
            var result = await DownloadOneCandidateAsync(threadId, postId, imageDir, item, author, token);
            if (result != null)
                downloaded.Add(result);
        });

        return downloaded.OrderBy(d => d.File, StringComparer.Ordinal).ToList();
    }

    public void SaveManifest(string threadId, List<DownloadedImage> items)
    {
        if (items.Count == 0)
            return;

        var imageDir = GetThreadImageDir(threadId);
        var manifestPath = Path.Combine(imageDir, "manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(items, JsonOpts));
    }
}
