using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MajesticParser.Services;

// Перенос URL-утилит: normalize_url, normalize_for_compare, extract_id_from_url,
// normalize_image_url, is_allowed_image_url
public static class UrlHelper
{
    private static readonly Regex IdRegex = new(@"\.(\d+)/?$", RegexOptions.Compiled);

    public static string NormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "";

        url = url.Trim();
        // urljoin(BASE_URL, url)
        if (Uri.TryCreate(new Uri(AppConstants.BaseUrl), url, out var combined))
            return combined.ToString();

        return url;
    }

    public static string NormalizeForCompare(string url)
    {
        return NormalizeUrl(url).TrimEnd('/').ToLowerInvariant();
    }

    public static string ExtractIdFromUrl(string url)
    {
        var m = IdRegex.Match((url ?? "").TrimEnd('/'));
        return m.Success ? m.Groups[1].Value : "0";
    }

    // Удаляет параметры размера (width/height/quality) если включено
    public static string NormalizeImageUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
            return "";

        if (!AppConstants.ImageRemoveSizeParams)
            return url;

        try
        {
            var uri = new Uri(url);
            var rawQuery = uri.Query.TrimStart('?');

            if (string.IsNullOrEmpty(rawQuery))
                return url;

            var keep = new List<string>();
            foreach (var pair in rawQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var key = eq >= 0 ? pair.Substring(0, eq) : pair;
                var lower = Uri.UnescapeDataString(key).ToLowerInvariant();
                if (lower is "width" or "height" or "quality")
                    continue;
                keep.Add(pair);
            }

            var newQuery = keep.Count > 0 ? "?" + string.Join("&", keep) : "";
            return $"{uri.GetLeftPart(UriPartial.Path)}{newQuery}{uri.Fragment}";
        }
        catch
        {
            return url;
        }
    }

    public static bool IsAllowedImageUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
            return false;

        string domain, path;
        try
        {
            var uri = new Uri(url);
            domain = uri.Host.ToLowerInvariant();
            path = uri.AbsolutePath.ToLowerInvariant();
        }
        catch
        {
            return false;
        }

        if (AppConstants.ImageAllowedDomains.Count > 0 &&
            !AppConstants.ImageAllowedDomains.Any(d => d.ToLowerInvariant() == domain))
            return false;

        if (AppConstants.ImageBlockedDomains.Any(d => d.ToLowerInvariant() == domain))
            return false;

        if (AppConstants.BlockedImagePathParts.Any(part => path.Contains(part)))
            return false;

        return true;
    }
}
