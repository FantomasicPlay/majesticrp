using System.Linq;
using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace MajesticParser.Services;

// Перенос: extract_text_from_message, extract_post_id
public static class MessageParser
{
    private static readonly Regex MultiNewline = new(@"\n{3,}", RegexOptions.Compiled);
    private static readonly Regex PostDash = new(@"/post-(\d+)", RegexOptions.Compiled);
    private static readonly Regex PostsSlash = new(@"/posts/(\d+)", RegexOptions.Compiled);
    private static readonly Regex PostIdAttr = new(@"post-(\d+)", RegexOptions.Compiled);
    private static readonly Regex AnyDigits = new(@"(\d+)", RegexOptions.Compiled);

    private static readonly string[] RemovableClasses =
    {
        "message-userExtras",
        "message-react",
        "message-attribution",
        "message-footer",
        "js-selectToQuoteEnd",
        "bbCodeBlock-expandLink"
    };

    public static string ExtractText(IElement message)
    {
        var content = HtmlHelper.GetMessageContent(message, fallbackToMessage: true);
        if (content == null)
            return "";

        // Спойлеры: заменить блок его содержимым (убирает кнопку/заголовок спойлера)
        foreach (var spoiler in content.QuerySelectorAll("div[class*=bbCodeSpoiler]").ToList())
        {
            if (spoiler.Parent == null)
                continue;

            var hidden = spoiler.QuerySelector("div[class*=bbCodeSpoiler-content]");
            if (hidden != null)
            {
                hidden.Remove();
                spoiler.Replace(hidden);
            }
            else
            {
                HtmlHelper.Unwrap(spoiler);
            }
        }

        // Удаляем служебные теги
        foreach (var tag in new[] { "script", "style", "nav", "footer", "aside", "header" })
            foreach (var el in content.QuerySelectorAll(tag).ToList())
                el.Remove();

        // Удаляем служебные блоки по классу
        foreach (var cls in RemovableClasses)
            foreach (var el in content.QuerySelectorAll($".{cls}").ToList())
                el.Remove();

        var text = HtmlHelper.GetSeparatedText(content, "\n");
        text = MultiNewline.Replace(text, "\n\n");
        return text.Trim();
    }

    public static string ExtractPostId(IElement message)
    {
        // 1) ссылка вида /post-123
        foreach (var a in message.QuerySelectorAll("a[href]"))
        {
            var href = a.GetAttribute("href") ?? "";
            var m = PostDash.Match(href);
            if (m.Success)
                return m.Groups[1].Value;
        }

        // 2) ссылка вида /posts/123
        foreach (var a in message.QuerySelectorAll("a[href]"))
        {
            var href = a.GetAttribute("href") ?? "";
            var m = PostsSlash.Match(href);
            if (m.Success)
                return m.Groups[1].Value;
        }

        // 3) id атрибут article: post-123
        var articleId = message.GetAttribute("id") ?? "";
        var im = PostIdAttr.Match(articleId);
        if (im.Success)
            return im.Groups[1].Value;

        // 4) data-атрибуты
        foreach (var attr in new[] { "data-content", "data-lb-id", "data-message-id" })
        {
            var val = message.GetAttribute(attr) ?? "";
            var dm = AnyDigits.Match(val);
            if (dm.Success)
                return dm.Groups[1].Value;
        }

        return "0";
    }
}
