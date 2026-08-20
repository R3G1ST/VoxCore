using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace VoxCore.Client;

public sealed record UpdateInfo(string Version, string Notes, string DownloadUrl);

public static class UpdateService
{
    private const string Repo = "https://api.github.com/repos/R3G1ST/VoxCore";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static string CurrentVersion
    {
        get
        {
            var v = typeof(UpdateService).Assembly.GetName().Version;
            return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public static async Task<UpdateInfo?> CheckAsync()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{Repo}/releases/latest");
        req.Headers.UserAgent.ParseAdd("VoxCore-Updater");
        using var resp = await Http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return null;
        var root = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = tag.TrimStart('v');
        if (Compare(version, CurrentVersion) <= 0) return null;

        string? url = null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (name.EndsWith(".exe") && name.Contains("Setup"))
            {
                url = a.GetProperty("browser_download_url").GetString();
                break;
            }
        }
        if (url is null) return null;
        var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        return new UpdateInfo(version, notes, url);
    }

    public static async Task DownloadAsync(string url, string destPath, IProgress<double>? progress = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("VoxCore-Updater");
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? 0;
        await using var src = await resp.Content.ReadAsStreamAsync();
        await using var dst = File.Create(destPath);
        var buf = new byte[81920];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buf)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n));
            read += n;
            if (total > 0) progress?.Report((double)read / total);
        }
    }

    public static void LaunchInstaller(string setupPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = setupPath,
            Arguments = "/S",
            UseShellExecute = true
        };
        Process.Start(psi);
    }

    private static int Compare(string a, string b)
    {
        var pa = a.Split('.');
        var pb = b.Split('.');
        for (int i = 0; i < 3; i++)
        {
            var x = int.TryParse(pa[i], out var xi) ? xi : 0;
            var y = int.TryParse(pb[i], out var yi) ? yi : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }
}
