using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows;

namespace AudioScrub;

static class UpdateChecker
{
    public const string CurrentVersion = "1.0.3";
    const string ApiUrl = "https://api.github.com/repos/yukkurinadi/YMM4AudioScrubPlugin/releases/latest";
    const string ReleasesUrl = "https://github.com/yukkurinadi/YMM4AudioScrubPlugin/releases";

    static int _started;

    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;
        _ = Task.Run(CheckAsync);
    }

    static async Task CheckAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("YMM4AudioScrubPlugin/" + CurrentVersion);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            var json = await http.GetStringAsync(ApiUrl);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // /releases/latest は Pre-release を返さないが、念のため除外する
            if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True)
                return;
            if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
                return;
            if (!root.TryGetProperty("tag_name", out var tagEl))
                return;

            var tag = tagEl.GetString();
            if (string.IsNullOrWhiteSpace(tag))
                return;

            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
            {
                AudioScrubLog.Write("update tag parse failed: " + tag);
                return;
            }
            if (!Version.TryParse(CurrentVersion, out var current))
                return;
            if (latest <= current)
            {
                AudioScrubLog.Write($"up to date current={current} latest={latest}");
                return;
            }

            for (var i = 0; i < 50 && Application.Current is null; i++)
                await Task.Delay(200);

            var app = Application.Current;
            if (app is null)
                return;

            await app.Dispatcher.InvokeAsync(() => Show(latest));
        }
        catch (Exception ex)
        {
            AudioScrubLog.Write("update check: " + ex.Message);
        }
    }

    static void Show(Version latest)
    {
        var result = MessageBox.Show(
            $"Audio Scrub の新しいバージョンがあります。\n\n現在: v{CurrentVersion}\n最新: v{latest}\n\nリリースページを開きますか？",
            "Audio Scrub アップデート",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (result != MessageBoxResult.Yes)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(ReleasesUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AudioScrubLog.Write("open releases: " + ex.Message);
        }
    }
}
