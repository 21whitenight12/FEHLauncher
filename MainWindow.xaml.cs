using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace FalloutLauncher
{
    public partial class MainWindow : Window
    {
        private const string PROFILE_NAME = "ChikadoZ";
        private const string MODS_FOLDER = "Main Fallout Catalog";
        private const long MAX_SAVES_SIZE = 10L * 1024 * 1024 * 1024; // 10 GB

        private bool isDarkTheme = true;
        private Process? mo2Process;

        public MainWindow()
        {
            InitializeComponent();
            ApplyTheme();
            UpdateSavesSizeDisplay();
        }

        // ========== Тема и логгирование ==========
        private void BtnTheme_Click(object sender, RoutedEventArgs e)
        {
            isDarkTheme = !isDarkTheme;
            btnTheme.Content = isDarkTheme ? "☀️" : "🌙";
            ApplyTheme();
        }

        private void BtnDiscord_Click(object sender, RoutedEventArgs e)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "https://discord.gg/UsCu5gXCJS",
                UseShellExecute = true
            };
            Process.Start(psi);
        }

        private void ApplyTheme()
        {
            var bgBrush = isDarkTheme
                ? (SolidColorBrush)this.Resources["WindowBackgroundDark"]
                : (SolidColorBrush)this.Resources["WindowBackgroundLight"];
            var fgBrush = isDarkTheme
                ? (SolidColorBrush)this.Resources["TextForegroundDark"]
                : (SolidColorBrush)this.Resources["TextForegroundLight"];
            var btnBrush = isDarkTheme
                ? (SolidColorBrush)this.Resources["ButtonBackgroundDark"]
                : (SolidColorBrush)this.Resources["ButtonBackgroundLight"];
            var logBrush = isDarkTheme
                ? (SolidColorBrush)this.Resources["LogBackgroundDark"]
                : (SolidColorBrush)this.Resources["LogBackgroundLight"];
            var trackBrush = isDarkTheme
                ? (SolidColorBrush)this.Resources["ProgressTrackDark"]
                : (SolidColorBrush)this.Resources["ProgressTrackLight"];

            this.Resources["WindowBackground"] = bgBrush;
            this.Resources["TextForeground"] = fgBrush;
            this.Resources["ButtonBackground"] = btnBrush;
            this.Resources["LogBackground"] = logBrush;
            this.Resources["ProgressTrack"] = trackBrush;

            this.Background = bgBrush;
            txtStatus.Foreground = fgBrush;
            btnLaunch.Background = btnBrush;
            btnMo2Only.Background = btnBrush;
            btnOpenSaves.Background = btnBrush;
            btnMods.Background = btnBrush;
        }

        private void Log(string message)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            string logEntry = $"[{timestamp}] {message}{Environment.NewLine}";
            Dispatcher.Invoke(() =>
            {
                txtLog.AppendText(logEntry);
                txtLog.ScrollToEnd();
                txtStatus.Text = message;
            });
        }

        private void ShowErrorAndExit(string message)
        {
            Log($"ОШИБКА: {message}");
            MessageBox.Show(message, "Ошибка запуска", MessageBoxButton.OK, MessageBoxImage.Error);
            Environment.Exit(1);
        }

        // ========== Автоматическая настройка MO2 ==========
        private void EnsureGamePathInMo2Ini(string mo2Dir, string gamePath)
        {
            string iniPath = Path.Combine(mo2Dir, "ModOrganizer.ini");
            if (!File.Exists(iniPath))
            {
                File.WriteAllText(iniPath, $"[General]\r\ngamePath={gamePath}\r\n");
                Log("Создан ModOrganizer.ini с путём к игре.");
                return;
            }

            var lines = File.ReadAllLines(iniPath).ToList();
            bool found = false;
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].StartsWith("gamePath="))
                {
                    lines[i] = $"gamePath={gamePath}";
                    found = true;
                    break;
                }
            }
            if (!found)
                lines.Add($"gamePath={gamePath}");

            File.WriteAllLines(iniPath, lines);
            Log($"Путь к игре в MO2 установлен: {gamePath}");
        }

        private void EnsureF4seExecutableInMo2Ini(string mo2Dir, string f4sePath)
        {
            string iniPath = Path.Combine(mo2Dir, "ModOrganizer.ini");
            if (!File.Exists(iniPath))
            {
                File.WriteAllText(iniPath,
                    "[General]\r\n" +
                    "[customExecutables]\r\n" +
                    "1\\title=F4SE\r\n" +
                    $"1\\binary={f4sePath}\r\n" +
                    "1\\arguments=\r\n" +
                    "1\\workingDirectory=\r\n" +
                    "1\\closeOnStart=false\r\n" +
                    "1\\steamAppID=\r\n" +
                    "size=1\r\n");
                Log("Добавлен исполняемый файл F4SE в ModOrganizer.ini.");
                return;
            }

            var lines = File.ReadAllLines(iniPath).ToList();
            bool f4seEntryExists = false;
            foreach (var line in lines)
            {
                if (line.Contains("\\title=F4SE"))
                {
                    f4seEntryExists = true;
                    break;
                }
            }

            if (f4seEntryExists)
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    if (lines[i].Trim().EndsWith("\\title=F4SE"))
                    {
                        string prefix = lines[i].Trim().Substring(0, lines[i].Trim().IndexOf("\\title=F4SE"));
                        for (int j = i; j < lines.Count; j++)
                        {
                            if (lines[j].StartsWith(prefix + "\\binary="))
                            {
                                lines[j] = $"{prefix}\\binary={f4sePath}";
                                Log($"Путь к F4SE в ModOrganizer.ini обновлён: {f4sePath}");
                                break;
                            }
                            if (lines[j].StartsWith("size=") || lines[j].StartsWith("["))
                                break;
                        }
                        break;
                    }
                }
            }
            else
            {
                int customSectionIndex = lines.FindIndex(l => l.Trim().Equals("[customExecutables]", StringComparison.OrdinalIgnoreCase));
                if (customSectionIndex == -1)
                {
                    lines.Add("[customExecutables]");
                    customSectionIndex = lines.Count - 1;
                }

                int maxIndex = 0;
                for (int i = customSectionIndex + 1; i < lines.Count; i++)
                {
                    if (lines[i].StartsWith("[")) break;
                    var match = Regex.Match(lines[i], @"^(\d+)\\");
                    if (match.Success && int.TryParse(match.Groups[1].Value, out int idx))
                        if (idx > maxIndex) maxIndex = idx;
                }
                int newIndex = maxIndex + 1;

                var newEntry = new[]
                {
                    $"{newIndex}\\title=F4SE",
                    $"{newIndex}\\binary={f4sePath}",
                    $"{newIndex}\\arguments=",
                    $"{newIndex}\\workingDirectory=",
                    $"{newIndex}\\closeOnStart=false",
                    $"{newIndex}\\steamAppID="
                };
                lines.InsertRange(customSectionIndex + 1, newEntry);

                int sizeLineIndex = lines.FindIndex(l => l.Trim().StartsWith("size="));
                if (sizeLineIndex != -1)
                    lines[sizeLineIndex] = $"size={newIndex}";
                else
                    lines.Insert(customSectionIndex + 1 + newEntry.Length, $"size={newIndex}");

                Log($"Исполняемый файл F4SE добавлен в ModOrganizer.ini (индекс {newIndex}).");
            }

            File.WriteAllLines(iniPath, lines);
        }

        private void EnsureF4seAsDefaultForProfile(string profilePath, string f4seFullPath)
        {
            string settingsIni = Path.Combine(profilePath, "settings.ini");
            if (!File.Exists(settingsIni))
            {
                File.WriteAllText(settingsIni, $"custom_executable={f4seFullPath}\r\n");
                Log($"Создан settings.ini для профиля с f4se_loader.exe");
                return;
            }

            var lines = File.ReadAllLines(settingsIni).ToList();
            bool found = false;
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].StartsWith("custom_executable="))
                {
                    lines[i] = $"custom_executable={f4seFullPath}";
                    found = true;
                    break;
                }
            }
            if (!found)
                lines.Add($"custom_executable={f4seFullPath}");

            File.WriteAllLines(settingsIni, lines);
            Log($"Для профиля {PROFILE_NAME} установлен запуск f4se_loader.exe.");
        }

        private bool PrepareLaunchEnvironment(out string mo2Path, out string mo2Dir, out string f4sePath)
        {
            mo2Path = string.Empty;
            mo2Dir = AppDomain.CurrentDomain.BaseDirectory;
            f4sePath = string.Empty;

            Log("Поиск установленной игры Fallout 4...");
            string? gamePath = FindGamePath();
            if (string.IsNullOrEmpty(gamePath))
            {
                ShowErrorAndExit("Не удалось найти установленную игру Fallout 4.");
                return false;
            }
            Log($"Игра найдена: {gamePath}");

            mo2Path = Path.Combine(mo2Dir, "ModOrganizer.exe");
            string profilesDir = Path.Combine(mo2Dir, "profiles");
            string profilePath = Path.Combine(profilesDir, PROFILE_NAME);

            if (!File.Exists(mo2Path))
            {
                ShowErrorAndExit($"ModOrganizer.exe не найден в папке: {mo2Dir}");
                return false;
            }
            Log("MO2 найден.");

            if (!Directory.Exists(profilePath))
            {
                ShowErrorAndExit($"Профиль \"{PROFILE_NAME}\" не найден в {profilesDir}");
                return false;
            }
            Log($"Профиль {PROFILE_NAME} найден.");

            string modsPath = Path.Combine(mo2Dir, "mods", MODS_FOLDER);
            if (!Directory.Exists(modsPath))
            {
                ShowErrorAndExit($"Папка \"{MODS_FOLDER}\" не найдена в MO2\\mods. Root Builder не установлен.");
                return false;
            }
            Log($"Папка Root Builder найдена: {modsPath}");

            f4sePath = Path.Combine(modsPath, "Root", "f4se_loader.exe");
            if (!File.Exists(f4sePath))
            {
                ShowErrorAndExit($"f4se_loader.exe не найден в {modsPath}\\Root");
                return false;
            }
            Log("F4SE найден.");

            EnsureGamePathInMo2Ini(mo2Dir, gamePath);
            EnsureF4seExecutableInMo2Ini(mo2Dir, f4sePath);
            EnsureF4seAsDefaultForProfile(profilePath, f4sePath);

            if (Process.GetProcessesByName("ModOrganizer").Length > 0)
            {
                ShowErrorAndExit("MO2 уже запущен. Закройте его вручную.");
                return false;
            }
            Log("MO2 не запущен.");

            return true;
        }

        // ========== Запуск игры (F4SE) ==========
        private async void BtnLaunch_Click(object sender, RoutedEventArgs e)
        {
            btnLaunch.IsEnabled = false;
            btnMo2Only.IsEnabled = false;
            txtLog.Clear();

            try
            {
                if (!PrepareLaunchEnvironment(out string mo2Path, out string mo2Dir, out string f4sePath))
                    return;

                string arguments = $"-p \"{PROFILE_NAME}\" \"{f4sePath}\"";
                var startInfo = new ProcessStartInfo
                {
                    FileName = mo2Path,
                    Arguments = arguments,
                    WorkingDirectory = mo2Dir,
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Normal
                };

                Log($"Запуск: {mo2Path} {arguments}");
                Log("Запрос прав администратора...");

                mo2Process = Process.Start(startInfo);
                if (mo2Process == null)
                {
                    ShowErrorAndExit("Не удалось запустить MO2.");
                    return;
                }

                Log($"MO2 запущен (PID: {mo2Process.Id}). Ожидание завершения...");
                await mo2Process.WaitForExitAsync();
                Log("MO2 завершил работу. Можно запускать снова.");
                UpdateSavesSizeDisplay();
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                Log("Запуск отменён пользователем (отказ в правах администратора).");
                txtStatus.Text = "Запуск отменён";
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
                btnMo2Only.IsEnabled = true;
                if (txtStatus.Text != "Запуск отменён")
                    txtStatus.Text = "Готов к запуску";
            }
        }

        // ========== Запуск только MO2 ==========
        private async void BtnLaunchMo2_Click(object sender, RoutedEventArgs e)
        {
            btnLaunch.IsEnabled = false;
            btnMo2Only.IsEnabled = false;
            txtLog.Clear();

            try
            {
                if (!PrepareLaunchEnvironment(out string mo2Path, out string mo2Dir, out string _))
                    return;

                string arguments = $"-p \"{PROFILE_NAME}\"";
                var startInfo = new ProcessStartInfo
                {
                    FileName = mo2Path,
                    Arguments = arguments,
                    WorkingDirectory = mo2Dir,
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Normal
                };

                Log($"Запуск: {mo2Path} {arguments}");
                Log("Запрос прав администратора...");

                mo2Process = Process.Start(startInfo);
                if (mo2Process == null)
                {
                    ShowErrorAndExit("Не удалось запустить MO2.");
                    return;
                }

                Log($"MO2 запущен (PID: {mo2Process.Id}). Ожидание завершения...");
                await mo2Process.WaitForExitAsync();
                Log("MO2 завершил работу.");
                UpdateSavesSizeDisplay();
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                Log("Запуск отменён пользователем (отказ в правах администратора).");
                txtStatus.Text = "Запуск отменён";
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
                btnMo2Only.IsEnabled = true;
                if (txtStatus.Text != "Запуск отменён")
                    txtStatus.Text = "Готов к запуску";
            }
        }

        // ========== Поиск пути к Fallout 4 ==========
        private string? FindGamePath()
        {
            try
            {
                using var key64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var subKey64 = key64.OpenSubKey(@"SOFTWARE\Bethesda Softworks\Fallout4");
                if (subKey64 != null)
                {
                    var path = subKey64.GetValue("Installed Path") as string;
                    if (!string.IsNullOrEmpty(path) && File.Exists(Path.Combine(path, "Fallout4.exe")))
                        return path;
                }
            }
            catch { }

            try
            {
                using var key32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
                using var subKey32 = key32.OpenSubKey(@"SOFTWARE\Bethesda Softworks\Fallout4");
                if (subKey32 != null)
                {
                    var path = subKey32.GetValue("Installed Path") as string;
                    if (!string.IsNullOrEmpty(path) && File.Exists(Path.Combine(path, "Fallout4.exe")))
                        return path;
                }
            }
            catch { }

            try
            {
                using var steamKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
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

        // ========== Расчёт размера папки сохранений ==========
        private long GetDirectorySize(string path)
        {
            if (!Directory.Exists(path)) return 0;

            long size = 0;
            try
            {
                foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { size += new FileInfo(file).Length; }
                    catch { }
                }
            }
            catch { }
            return size;
        }

        private void UpdateSavesSizeDisplay()
        {
            string savesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "profiles", PROFILE_NAME, "saves");
            long sizeBytes = GetDirectorySize(savesPath);
            double percentage = Math.Min(1.0, (double)sizeBytes / MAX_SAVES_SIZE);

            pbSaves.Value = percentage * 100;
            double sizeGB = sizeBytes / (1024.0 * 1024.0 * 1024.0);
            lblSavesSize.Text = $"{sizeGB:F2} GB / 10.00 GB";

            // Цвет прогресс-бара в зависимости от заполненности
            if (percentage >= 0.8)
                pbSaves.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36)); // красный
            else if (percentage >= 0.5)
                pbSaves.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00)); // оранжевый
            else
                pbSaves.Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)); // зелёный
        }

        private void BtnOpenSaves_Click(object sender, RoutedEventArgs e)
        {
            string savesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "profiles", PROFILE_NAME, "saves");
            if (Directory.Exists(savesPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = savesPath,
                    UseShellExecute = true
                });
            }
            else
            {
                Log("Папка saves не найдена.");
            }
        }

        private void BtnMods_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Функция управления модами в разработке.",
                "В разработке", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}