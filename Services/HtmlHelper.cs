using System.Collections.Generic;
using System.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace MajesticParser.Services;

// Утилиты для работы с AngleSharp как замена BeautifulSoup
public static class HtmlHelper
{
    private static readonly HtmlParser Parser = new();

    public static IHtmlDocument Parse(string html) => Parser.ParseDocument(html);

    // Аналог BeautifulSoup get_text(separator=sep, strip=True):
    // собрать все текстовые узлы, обрезать пробелы у каждого, выкинуть пустые, склеить через sep.
    public static string GetSeparatedText(IElement element, string separator)
    {
        var parts = element.GetDescendants()
            .OfType<IText>()
            .Select(t => t.Text.Trim())
            .Where(s => s.Length > 0);
        return string.Join(separator, parts);
    }

    // Аналог BeautifulSoup unwrap(): заменить узел его дочерними узлами.
    public static void Unwrap(IElement element)
    {
        var parent = element.Parent;
        if (parent == null)
            return;

        while (element.FirstChild != null)
            parent.InsertBefore(element.FirstChild, element);

        element.Remove();
    }

    // get_message_content: тело пользовательского сообщения
    public static IElement? GetMessageContent(IElement message, bool fallbackToMessage = false)
    {
        var content =
            message.QuerySelector("div.message-content")
            ?? message.QuerySelector("div.bbWrapper")
            ?? message.QuerySelector("div.message-body");

        if (content == null && fallbackToMessage)
            return message;

        return content;
    }
}
