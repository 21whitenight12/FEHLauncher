using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace FalloutLauncher
{
    public partial class MainWindow : Window
    {
        private const string PROFILE_NAME = "ChikadoZ";
        private const string MODS_FOLDER = "Main Fallout Catalog";
        // Внутренний ID Fallout4 всегда без пробелов. Второй вариант на всякий случай.
        private readonly string[] GAME_IDS = { "Fallout4", "Fallout 4" };

        private bool isDarkTheme = false;
        private Process? mo2Process;

        public MainWindow()
        {
            InitializeComponent();
            ApplyTheme();
        }

        private void BtnTheme_Click(object sender, RoutedEventArgs e)
        {
            isDarkTheme = btnTheme.IsChecked == true;
            btnTheme.Content = isDarkTheme ? "☀️ Светлая тема" : "🌙 Тёмная тема";
            ApplyTheme();
        }

        private void ApplyTheme()
        {
            var bgBrush = isDarkTheme
                ? (SolidColorBrush)Application.Current.Resources["WindowBackgroundDark"]
                : (SolidColorBrush)Application.Current.Resources["WindowBackgroundLight"];

            var fgBrush = isDarkTheme
                ? (SolidColorBrush)Application.Current.Resources["TextForegroundDark"]
                : (SolidColorBrush)Application.Current.Resources["TextForegroundLight"];

            var btnBrush = isDarkTheme
                ? (SolidColorBrush)Application.Current.Resources["ButtonBackgroundDark"]
                : (SolidColorBrush)Application.Current.Resources["ButtonBackgroundLight"];

            Application.Current.Resources["WindowBackground"] = bgBrush;
            Application.Current.Resources["TextForeground"] = fgBrush;
            Application.Current.Resources["ButtonBackground"] = btnBrush;
        }

        private void Log(string message)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            string logEntry = $"[{timestamp}] {message}{Environment.NewLine}";
            txtLog.AppendText(logEntry);
            txtLog.ScrollToEnd();
            txtStatus.Text = message;
        }

        private void ShowErrorAndExit(string message)
        {
            Log($"ОШИБКА: {message}");
            MessageBox.Show(message, "Ошибка запуска", MessageBoxButton.OK, MessageBoxImage.Error);
            Environment.Exit(1);
        }

        private async void BtnLaunch_Click(object sender, RoutedEventArgs e)
        {
            btnLaunch.IsEnabled = false;
            txtLog.Clear();

            try
            {
                // 1. Определяем путь к игре
                Log("Поиск установленной игры...");
                string? gamePath = FindGamePath();
                if (string.IsNullOrEmpty(gamePath))
                {
                    ShowErrorAndExit("Не удалось найти установленную игру Fallout 4.");
                    return;
                }
                Log($"Игра найдена: {gamePath}");

                // 2. Проверяем пути MO2
                string mo2Dir = AppDomain.CurrentDomain.BaseDirectory;
                string mo2Path = Path.Combine(mo2Dir, "ModOrganizer.exe");
                string profilesDir = Path.Combine(mo2Dir, "profiles");
                string profilePath = Path.Combine(profilesDir, PROFILE_NAME);

                if (!File.Exists(mo2Path))
                {
                    ShowErrorAndExit($"ModOrganizer.exe не найден в папке: {mo2Dir}");
                    return;
                }
                Log("MO2 найден.");

                if (!Directory.Exists(profilePath))
                {
                    ShowErrorAndExit($"Профиль \"{PROFILE_NAME}\" не найден в {profilesDir}");
                    return;
                }
                Log($"Профиль {PROFILE_NAME} найден.");

                // 3. Проверяем папку модов (предупреждение)
                string modsPath = Path.Combine(mo2Dir, "mods", MODS_FOLDER);
                if (!Directory.Exists(modsPath))
                {
                    Log($"Внимание: папка \"{MODS_FOLDER}\" не найдена в MO2\\mods. Root Builder может не сработать.");
                }

                // 4. Проверяем, не запущен ли MO2
                if (Process.GetProcessesByName("ModOrganizer").Any())
                {
                    ShowErrorAndExit("MO2 уже запущен. Закройте его вручную.");
                    return;
                }
                Log("MO2 не запущен, запускаем...");

                // 5. Запускаем MO2 с разными ID игры
                bool launched = false;
                foreach (var gameId in GAME_IDS)
                {
                    // Добавляем кавычки только если в ID есть пробел
                    string safeGameId = gameId.Contains(" ") ? $"\"{gameId}\"" : gameId;
                    string args = $"-p {PROFILE_NAME} -g {safeGameId} -m";

                    Log($"Пробуем аргументы: {args}");

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = mo2Path,
                        Arguments = args,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };

                    mo2Process = new Process { StartInfo = startInfo };

                    try
                    {
                        mo2Process.Start();
                        Log($"MO2 запущен (PID: {mo2Process.Id}). Ожидание завершения игры...");

                        // Ждём 1 секунду. Если MO2 завершился мгновенно, значит аргумент неверный.
                        await Task.Delay(1000);

                        if (mo2Process.HasExited)
                        {
                            Log($"MO2 завершился с кодом {mo2Process.ExitCode}. Пробуем следующий вариант...");
                            mo2Process.Dispose();
                            mo2Process = null;
                            continue;
                        }

                        launched = true;
                        Log($"Аргумент -g {gameId} сработал. Ожидаем закрытия игры...");
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log($"Ошибка запуска: {ex.Message}");
                        mo2Process?.Dispose();
                        mo2Process = null;
                    }
                }

                if (!launched)
                {
                    ShowErrorAndExit("Не удалось запустить MO2 ни с одним из аргументов.");
                    return;
                }

                // 6. Ожидаем завершения MO2
                await mo2Process.WaitForExitAsync();
                Log("MO2 завершил работу. Игра закрыта.");
            }
            catch (Exception ex)
            {
                ShowErrorAndExit($"Критическая ошибка: {ex.Message}");
            }
            finally
            {
                mo2Process?.Dispose();
                mo2Process = null;
                btnLaunch.IsEnabled = true;
                txtStatus.Text = "Готов к запуску";
            }
        }

        private string? FindGamePath()
        {
            // 1. Реестр (64-bit)
            try
            {
                using var key64 = RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, RegistryView.Registry64);
                using var subKey64 = key64.OpenSubKey(@"SOFTWARE\Bethesda Softworks\Fallout4");
                if (subKey64 != null)
                {
                    var path = subKey64.GetValue("Installed Path") as string;
                    if (!string.IsNullOrEmpty(path) && File.Exists(Path.Combine(path, "Fallout4.exe")))
                        return path;
                }
            }
            catch { }

            // 2. Реестр (32-bit / WOW6432Node)
            try
            {
                using var key32 = RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, RegistryView.Registry32);
                using var subKey32 = key32.OpenSubKey(@"SOFTWARE\Bethesda Softworks\Fallout4");
                if (subKey32 != null)
                {
                    var path = subKey32.GetValue("Installed Path") as string;
                    if (!string.IsNullOrEmpty(path) && File.Exists(Path.Combine(path, "Fallout4.exe")))
                        return path;
                }
            }
            catch { }

            // 3. Steam
            try
            {
                using var steamKey = RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, RegistryView.Registry32)
                    .OpenSubKey(@"SOFTWARE\Valve\Steam");
                if (steamKey != null)
                {
                    var steamPath = steamKey.GetValue("InstallPath") as string;
                    if (!string.IsNullOrEmpty(steamPath))
                    {
                        var vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                        if (File.Exists(vdfPath))
                        {
                            var vdfContent = File.ReadAllText(vdfPath);
                            var matches = Regex.Matches(vdfContent, @"""path""\s+""([^""]+)""");
                            foreach (Match match in matches)
                            {
                                var libPath = match.Groups[1].Value.Replace(@"\\", @"\");
                                var fo4Path = Path.Combine(libPath, "steamapps", "common", "Fallout 4");
                                if (Directory.Exists(fo4Path) && File.Exists(Path.Combine(fo4Path, "Fallout4.exe")))
                                    return fo4Path;
                            }
                        }
                    }
                }
            }
            catch { }

            // 4. Ручной выбор
            Log("Игра не найдена автоматически. Выберите папку вручную.");
            var dialog = new OpenFolderDialog
            {
                Title = "Выберите папку с Fallout 4 (где лежит Fallout4.exe)"
            };
            if (dialog.ShowDialog() == true)
            {
                var selectedPath = dialog.FolderName;
                if (File.Exists(Path.Combine(selectedPath, "Fallout4.exe")))
                    return selectedPath;
            }

            return null;
        }
    }
}