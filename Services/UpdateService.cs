using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace MajesticParser.Services;

// Автообновление через GitHub Releases.
// Проверяет последний релиз, сравнивает версию, качает и запускает установщик.
public class UpdateService
{
    // Репозиторий на GitHub (owner/repo)
    public const string Owner = "FantomasicPlay";
    public const string Repo = "majesticrp";

    private static readonly HttpClient Http = new();

    static UpdateService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("MajesticParser-Updater");
        Http.Timeout = TimeSpan.FromSeconds(20);
    }

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0);

    // Возвращает (версия, ссылка на .exe установщик), если есть более новая версия
    public async Task<(Version version, string url)?> CheckAsync()
    {
        try
        {
            var api = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
            using var resp = await Http.GetAsync(api);
            if (!resp.IsSuccessStatusCode)
                return null;

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.GetProperty("tag_name").GetString() ?? "";
            var verStr = tag.TrimStart('v', 'V');
            if (!Version.TryParse(verStr, out var latest))
                return null;

            if (latest <= CurrentVersion)
                return null;

            if (!root.TryGetProperty("assets", out var assets))
                return null;

            foreach (var a in assets.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    var url = a.GetProperty("browser_download_url").GetString();
                    if (!string.IsNullOrEmpty(url))
                        return (latest, url);
                }
            }
            return null;
        }
        catch
        {
            return null; // нет сети / нет релизов / приватный репо — тихо
        }
    }

    public async Task<string?> DownloadAsync(string url, Action<int>? progress = null,
        System.Threading.CancellationToken ct = default)
    {
        try
        {
            var tmp = Path.Combine(Path.GetTempPath(), "MajesticParser_Update.exe");
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            var total = resp.Content.Headers.ContentLength ?? -1;
            await using var input = await resp.Content.ReadAsStreamAsync(ct);
            await using var output = File.Create(tmp);

            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (total > 0)
                    progress?.Invoke((int)(read * 100 / total));
            }
            return tmp;
        }
        catch
        {
            return null;
        }
    }

    // Запустить установщик и закрыть приложение.
    // silent = тихая установка с автоперезапуском (для автообновления при запуске).
    public void RunInstallerAndExit(string installerPath, bool silent = false)
    {
        var psi = new ProcessStartInfo(installerPath) { UseShellExecute = true };
        if (silent)
            psi.Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART";
        Process.Start(psi);
        Application.Current.Shutdown();
    }
}
