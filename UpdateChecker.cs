using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace FalloutLauncher
{
    /// <summary>
    /// Проверка и скачивание обновлений с GitHub Releases.
    /// Репозиторий: 21whitenight12/FEHLauncher
    /// </summary>
    public class UpdateChecker
    {
        private const string REPO_OWNER = "21whitenight12";
        private const string REPO_NAME = "FEHLauncher";
        private static readonly string GITHUB_API =
            $"https://api.github.com/repos/{REPO_OWNER}/{REPO_NAME}/releases/latest";

        private readonly HttpClient _httpClient;
        private string? _downloadUrl;
        private string? _latestVersion;

        /// <summary>Последняя доступная версия (без префикса v).</summary>
        public string? LatestVersion => _latestVersion;

        /// <summary>Есть ли обновление.</summary>
        public bool UpdateAvailable { get; private set; }

        /// <summary>Текущая версия приложения из Assembly.</summary>
        public static string CurrentVersion
        {
            get
            {
                var ver = Assembly.GetExecutingAssembly().GetName().Version;
                return ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "0.0.0";
            }
        }

        public UpdateChecker()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("FEHLauncher/2.0");
            _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github.v3+json");
            _httpClient.Timeout = TimeSpan.FromSeconds(10);
        }

        /// <summary>
        /// Проверить наличие обновления на GitHub.
        /// </summary>
        /// <returns>true, если обновление доступно.</returns>
        public async Task<bool> CheckForUpdateAsync()
        {
            try
            {
                var response = await _httpClient.GetStringAsync(GITHUB_API);
                using var json = JsonDocument.Parse(response);
                var root = json.RootElement;

                var tagName = root.GetProperty("tag_name").GetString();
                if (string.IsNullOrEmpty(tagName))
                    return false;

                _latestVersion = tagName.TrimStart('v', 'V');

                var currentVerStr = CurrentVersion;
                UpdateAvailable = CompareVersions(_latestVersion, currentVerStr) > 0;

                if (UpdateAvailable)
                {
                    var assets = root.GetProperty("assets");
                    foreach (var asset in assets.EnumerateArray())
                    {
                        var name = asset.GetProperty("name").GetString();
                        if (!string.IsNullOrEmpty(name) &&
                            name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            _downloadUrl = asset.GetProperty("browser_download_url").GetString();
                            break;
                        }
                    }
                }

                return UpdateAvailable;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Скачать обновление во временную папку.
        /// </summary>
        /// <param name="progress">Прогресс 0–100.</param>
        /// <returns>Путь к скачанному файлу или null при ошибке.</returns>
        public async Task<string?> DownloadUpdateAsync(IProgress<int>? progress = null)
        {
            if (string.IsNullOrEmpty(_downloadUrl))
                return null;

            var tempPath = Path.Combine(Path.GetTempPath(), "FEHLauncher_update.exe");

            using var response = await _httpClient.GetAsync(
                _downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            await using var contentStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[8192];
            long totalRead = 0;
            int bytesRead;
            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead);
                totalRead += bytesRead;
                if (totalBytes > 0)
                    progress?.Report((int)(totalRead * 100 / totalBytes));
            }

            return tempPath;
        }

        /// <summary>
        /// Запустить обновление: скрипт ждёт завершения процесса, заменяет exe и перезапускает.
        /// </summary>
        public static void ApplyUpdate(string updateFilePath)
        {
            var updaterPath = Path.Combine(Path.GetTempPath(), "FEHLauncher_updater.cmd");
            var currentExe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(currentExe))
                currentExe = "FEHLauncher.exe";

            var script =
                "@echo off\r\n" +
                "echo FEH Launcher: обновление...\r\n" +
                "ping 127.0.0.1 -n 4 > nul\r\n" +
                $"copy /Y \"{updateFilePath}\" \"{currentExe}\" > nul\r\n" +
                $"if exist \"{currentExe}\" (\r\n" +
                $"    start \"\" \"{currentExe}\"\r\n" +
                ")\r\n" +
                "del \"%~f0\"\r\n";

            File.WriteAllText(updaterPath, script);

            var psi = new ProcessStartInfo
            {
                FileName = updaterPath,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                UseShellExecute = true
            };
            Process.Start(psi);
            Environment.Exit(0);
        }

        /// <summary>
        /// Сравнение версий по семантике A.B.C.
        /// Возвращает &gt;0 если a &gt; b, 0 если равны, &lt;0 если a &lt; b.
        /// </summary>
        private static int CompareVersions(string a, string b)
        {
            var partsA = a.Split('.');
            var partsB = b.Split('.');
            int max = Math.Max(partsA.Length, partsB.Length);
            for (int i = 0; i < max; i++)
            {
                int va = i < partsA.Length && int.TryParse(partsA[i], out var x) ? x : 0;
                int vb = i < partsB.Length && int.TryParse(partsB[i], out var y) ? y : 0;
                if (va != vb) return va.CompareTo(vb);
            }
            return 0;
        }
    }
}
