using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Principal;
using System.Threading;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;

namespace MajesticParser.Services;

// Перенос блока SELENIUM: get_driver, wait_page_loaded, safe_driver_get,
// apply_cookies_if_needed. Драйвер chromedriver резолвится Selenium Manager
// автоматически (встроен в Selenium 4.6+), поэтому отдельный кэш пути не нужен.
public class BrowserService : IDisposable
{
    private readonly Action<string> _log;
    private readonly string _profileDir;
    public ChromeDriver Driver { get; }

    // Запущено ли приложение с правами администратора (под ним Chrome не стартует)
    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public BrowserService(bool headless, Action<string> log)
    {
        _log = log;

        // Уникальный профиль на каждую сессию — чтобы не конфликтовать
        // с уже открытым обычным Chrome пользователя (иначе процесс может сразу выйти).
        _profileDir = Path.Combine(Path.GetTempPath(), "MajesticParser_" + Guid.NewGuid().ToString("N"));

        var options = new ChromeOptions();
        if (headless)
            options.AddArgument("--headless=new");

        options.AddArgument("--no-sandbox");
        options.AddArgument("--disable-dev-shm-usage");
        options.AddArgument("--disable-gpu");
        options.AddArgument("--disable-extensions");
        options.AddArgument("--no-first-run");
        options.AddArgument("--no-default-browser-check");
        options.AddArgument($"--user-data-dir={_profileDir}");
        options.AddArgument("--disable-blink-features=AutomationControlled");
        options.AddArgument($"--user-agent={AppConstants.UserAgent}");
        options.AddExcludedArgument("enable-automation");
        options.AddAdditionalOption("useAutomationExtension", false);

        var service = ChromeDriverService.CreateDefaultService();
        service.HideCommandPromptWindow = true;

        try
        {
            Driver = new ChromeDriver(service, options);
        }
        catch (WebDriverException e) when (e.Message.Contains("Chrome instance exited") ||
                                           e.Message.Contains("session not created"))
        {
            var hint = IsElevated()
                ? "Приложение запущено ОТ АДМИНИСТРАТОРА — Chrome не работает под админом. " +
                  "Закройте приложение и запустите его как обычный пользователь (без «Запуск от имени администратора»)."
                : "Не удалось запустить Chrome. Проверьте, что установлен Google Chrome и он обновлён.";
            throw new InvalidOperationException(hint, e);
        }

        Driver.ExecuteCdpCommand("Page.addScriptToEvaluateOnNewDocument",
            new Dictionary<string, object?>
            {
                ["source"] =
                    "Object.defineProperty(navigator, 'webdriver', {get: () => undefined});" +
                    "Object.defineProperty(navigator, 'plugins', {get: () => [1,2,3]});" +
                    "Object.defineProperty(navigator, 'languages', {get: () => ['ru-RU', 'ru', 'en-US', 'en']});"
            });
    }

    public bool SafeGet(string url, int retries = AppConstants.NavRetries)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt <= retries; attempt++)
        {
            try
            {
                Driver.Navigate().GoToUrl(url);
                return true;
            }
            catch (Exception e)
            {
                lastError = e;
                if (attempt < retries)
                    Thread.Sleep(2000);
            }
        }

        _log($"  ❌ Не удалось загрузить страницу {url}: {lastError?.Message}");
        return false;
    }

    public void WaitPageLoaded(int timeoutSeconds = 12)
    {
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                var state = (string?)((IJavaScriptExecutor)Driver)
                    .ExecuteScript("return document.readyState");
                if (state == "complete")
                    break;
                Thread.Sleep(200);
            }
        }
        catch
        {
            // ignore — как в Python
        }

        for (var i = 0; i < 8; i++)
        {
            var html = Driver.PageSource;
            if (!html.ToLowerInvariant().Contains("challenge") && html.Length > 5000)
                break;
            Thread.Sleep(1000);
        }
    }

    public void ApplyCookiesIfNeeded()
    {
        if (AppConstants.Cookies.Count == 0)
            return;

        if (!SafeGet(AppConstants.BaseUrl))
            return;
        WaitPageLoaded();

        foreach (var (name, value) in AppConstants.Cookies)
            Driver.Manage().Cookies.AddCookie(new Cookie(name, value));

        _log("🔐 Куки добавлены в браузер");
    }

    public bool HasNextPage()
    {
        try
        {
            var elements = Driver.FindElements(
                By.CssSelector("a.pageNav-jump--next, a[rel='next']"));
            foreach (var el in elements)
            {
                var href = el.GetAttribute("href");
                if (!string.IsNullOrEmpty(href))
                    return true;
            }
        }
        catch
        {
            return false;
        }
        return false;
    }

    public void Dispose()
    {
        try { Driver.Quit(); }
        catch { /* ignore */ }
        try { Driver.Dispose(); }
        catch { /* ignore */ }
        try
        {
            if (Directory.Exists(_profileDir))
                Directory.Delete(_profileDir, recursive: true);
        }
        catch { /* ignore */ }
    }
}
