using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
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
        private bool isMaximized = false;
        private WindowState savedState = WindowState.Normal;

        public MainWindow()
        {
            InitializeComponent();
            LoadWindowsAccentColor();
            ApplyTheme();
            UpdateSavesSizeDisplay();

            // Bug 4: Подставляем имя пользователя системы
            string userName = Environment.UserName;
            txtWelcome.Text = $"С возвращением, {userName}!";

            // Автопоиск игры при старте лаунчера
            Log("Поиск установленной игры Fallout 4...");
            string? gamePath = FindGamePath();
            if (!string.IsNullOrEmpty(gamePath))
                txtGamePath.Text = $"📂 {gamePath}";
            else
                Log("Игра не найдена автоматически. Выберите папку вручную при запуске.");
        }

        // ==================================================================
        // CUSTOM WINDOW CHROME — close, minimize, maximize, drag
        // ==================================================================

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState.Minimized;

        private void BtnMaximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && e.ClickCount == 1)
                this.DragMove();
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Maximized)
            {
                isMaximized = true;
                UpdateMaximizeIcon(false);
                // Compensate for maximized window border offset
                windowFrame.Margin = new Thickness(7);
            }
            else
            {
                isMaximized = false;
                UpdateMaximizeIcon(true);
                windowFrame.Margin = new Thickness(0);
            }
        }

        private void UpdateMaximizeIcon(bool isNormal)
        {
            if (btnMaximize == null) return;
            var rect = maxIcon;
            if (rect == null) return;

            if (isNormal)
            {
                // Maximize icon: hollow square
                rect.Width = 10;
                rect.Height = 10;
                rect.Stroke = FindResource("TextPrimaryBrush") as Brush ?? Brushes.White;
                rect.Fill = Brushes.Transparent;
                btnMaximize.ToolTip = "Развернуть";
            }
            else
            {
                // Restore icon: two overlapping squares
                rect.Width = 10;
                rect.Height = 10;
                rect.Stroke = FindResource("TextPrimaryBrush") as Brush ?? Brushes.White;
                rect.Fill = Brushes.Transparent;
                btnMaximize.ToolTip = "Восстановить";
            }
        }

        private void ToggleMaximize()
        {
            if (isMaximized)
            {
                WindowState = savedState;
            }
            else
            {
                savedState = WindowState;
                WindowState = WindowState.Maximized;
            }
        }

        // ==================================================================
        // SIDEBAR NAVIGATION
        // ==================================================================

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;

            var radio = sender as RadioButton;
            if (radio == null) return;

            // For now, only "Mods" tab opens the ModsWindow as a dialog.
            // Home / Saves / About stay on the main page.
            if (radio == navMods)
            {
                BtnMods_Click(sender, e);
                // Re-check Home after returning from ModsWindow
                navHome.IsChecked = true;
            }
            else if (radio == navSaves)
            {
                BtnOpenSaves_Click(sender, e);
                navHome.IsChecked = true;
            }
            else if (radio == navAbout)
            {
                ShowAbout();
                navHome.IsChecked = true;
            }
        }

        private void ShowAbout()
        {
            string mo2Dir = AppDomain.CurrentDomain.BaseDirectory;
            string gamePath = FindGamePath() ?? "не найден";

            MessageBox.Show(
                $"FNH Launcher by WhiteNight v1.1.0\n\n" +
                $"Лаунчер для Fallout 4 с поддержкой MO2 и F4SE\n\n" +
                $"Профиль: {PROFILE_NAME}\n" +
                $"MO2: {mo2Dir}\n" +
                $"Игра: {gamePath}\n\n" +
                $"Discord: discord.gg/UsCu5gXCJS",
                "О программе",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        // ==================================================================
        // LOG COLLAPSIBLE
        // ==================================================================

        private void LogToggle_Changed(object sender, RoutedEventArgs e)
        {
            // Guard against early calls during XAML loading before logContent is wired up
            if (logContent == null) return;

            bool isVisible = btnToggleLog.IsChecked == true;
            logContent.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;

            // Adjust log section height
            if (isVisible)
                logContent.Height = 140;
            else
                logContent.Height = 0;
        }

        // ==================================================================
        // THEME SYSTEM
        // ==================================================================

        private void BtnTheme_Click(object sender, RoutedEventArgs e)
        {
            isDarkTheme = !isDarkTheme;
            ApplyTheme();
        }

        private void LoadWindowsAccentColor()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
                if (key != null)
                {
                    int accentColor = (int)(key.GetValue("AccentColor", 0) ?? 0);
                    if (accentColor != 0)
                    {
                        // Registry хранит в формате ABGR (0xAABBGGRR), WPF нужен ARGB (0xAARRGGBB)
                        byte a = (byte)((accentColor >> 24) & 0xFF);
                        byte b = (byte)((accentColor >> 16) & 0xFF);
                        byte g = (byte)((accentColor >> 8) & 0xFF);
                        byte r = (byte)(accentColor & 0xFF);
                        var winAccent = Color.FromArgb(a, r, g, b);
                        Resources["AccentColor"] = winAccent;
                        Resources["AccentBrush"] = new SolidColorBrush(winAccent);
                        Log($"Акцентный цвет Windows: #{winAccent.R:X2}{winAccent.G:X2}{winAccent.B:X2}");
                    }
                }
            }
            catch { }
        }

        private void ApplyTheme()
        {
            // Choose color sets
            Color bg, surface, card, border, textPrimary, textSecondary, logBg, titleBg;

            if (isDarkTheme)
            {
                bg = (Color)FindResource("DarkBg");
                surface = (Color)FindResource("DarkSurface");
                card = (Color)FindResource("DarkCard");
                border = (Color)FindResource("DarkBorder");
                textPrimary = (Color)FindResource("DarkTextPrimary");
                textSecondary = (Color)FindResource("DarkTextSecondary");
                logBg = (Color)FindResource("DarkLogBg");
                titleBg = (Color)FindResource("DarkTitleBg");
                btnThemeIcon.Text = "🌙";
            }
            else
            {
                bg = (Color)FindResource("LightBg");
                surface = (Color)FindResource("LightSurface");
                card = (Color)FindResource("LightCard");
                border = (Color)FindResource("LightBorder");
                textPrimary = (Color)FindResource("LightTextPrimary");
                textSecondary = (Color)FindResource("LightTextSecondary");
                logBg = (Color)FindResource("LightLogBg");
                titleBg = (Color)FindResource("LightTitleBg");
                btnThemeIcon.Text = "☀️";
            }

            // Swap dynamic brushes
            SetBrush("BgBrush", bg);
            SetBrush("SurfaceBrush", surface);
            SetBrush("CardBrush", card);
            SetBrush("BorderBrush", border);
            SetBrush("TextPrimaryBrush", textPrimary);
            SetBrush("TextSecondaryBrush", textSecondary);
            SetBrush("LogBgBrush", logBg);
            SetBrush("TitleBgBrush", titleBg);

            // Force re-draw for window frame border
            var fg = isDarkTheme
                ? (Color)FindResource("DarkTextPrimary")
                : (Color)FindResource("LightTextPrimary");
            windowFrame.BorderBrush = new SolidColorBrush(border);
            windowFrame.Background = new SolidColorBrush(bg);

            // Update maximize icon colors
            UpdateMaximizeIcon(!isMaximized);

            // Swap hover overlay color (white for dark, black for light)
            Resources["HoverOverlayColor"] = isDarkTheme
                ? Color.FromRgb(0xFF, 0xFF, 0xFF)
                : Color.FromRgb(0x00, 0x00, 0x00);
        }

        private void SetBrush(string key, Color color)
        {
            // Always create a new brush — XAML-loaded brushes may be frozen/read-only
            Resources[key] = new SolidColorBrush(color);
        }

        // ==================================================================
        // LOGGING
        // ==================================================================

        private void Log(string message)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            string logEntry = $"[{timestamp}] {message}{Environment.NewLine}";
            Dispatcher.Invoke(() =>
            {
                txtLog.AppendText(logEntry);
                txtLog.ScrollToEnd();
                lblStatusBar.Text = message;
            });
        }

        private void ShowErrorAndExit(string message)
        {
            Log($"ОШИБКА: {message}");
            MessageBox.Show(message, "Ошибка запуска", MessageBoxButton.OK, MessageBoxImage.Error);
            Environment.Exit(1);
        }

        // ==================================================================
        // MO2 AUTO-CONFIGURATION (unchanged from original)
        // ==================================================================

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
                File.WriteAllText(settingsIni, $"custom_executable=F4SE\r\n");
                Log($"Создан settings.ini для профиля с f4se_loader.exe ({f4seFullPath})");
                return;
            }

            var lines = File.ReadAllLines(settingsIni).ToList();
            bool found = false;
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].StartsWith("custom_executable="))
                {
                    lines[i] = $"custom_executable=F4SE";
                    found = true;
                    break;
                }
            }
            if (!found)
                lines.Add($"custom_executable=F4SE");

            File.WriteAllLines(settingsIni, lines);
            Log($"Для профиля {PROFILE_NAME} установлен запуск f4se_loader.exe ({f4seFullPath}).");
        }

        // ==================================================================
        // LAUNCH LOGIC
        // ==================================================================

        /// <summary>
        /// Базовая подготовка — проверка игры, MO2, профиля. БЕЗ поиска F4SE.
        /// </summary>
        private bool PrepareBasicEnvironment(out string mo2Path, out string mo2Dir)
        {
            mo2Path = string.Empty;
            mo2Dir = AppDomain.CurrentDomain.BaseDirectory;

            Log("Поиск установленной игры Fallout 4...");
            string? gamePath = FindGamePath();
            if (string.IsNullOrEmpty(gamePath))
            {
                ShowErrorAndExit("Не удалось найти установленную игру Fallout 4.");
                return false;
            }
            Log($"Игра найдена: {gamePath}");

            // Update game path display in welcome card
            txtGamePath.Text = $"📂 {gamePath}";

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
                Log($"Папка модов \"{MODS_FOLDER}\" не найдена — будет создана.");
                Directory.CreateDirectory(modsPath);
            }

            // Устанавливаем путь к игре в MO2
            EnsureGamePathInMo2Ini(mo2Dir, gamePath);

            return true;
        }

        /// <summary>
        /// Подготовка F4SE — поиск f4se_loader.exe и настройка MO2.
        /// Возвращает false если F4SE не найден (но НЕ завершает приложение).
        /// </summary>
        private bool PrepareF4SEEnvironment(out string f4sePath)
        {
            string mo2Dir = AppDomain.CurrentDomain.BaseDirectory;
            string? gamePath = FindGamePath();
            f4sePath = string.Empty;

            // Расширенный поиск f4se_loader.exe
            string[] searchPaths = new[]
            {
                Path.Combine(gamePath ?? "", "f4se_loader.exe"),
                Path.Combine(mo2Dir, "f4se_loader.exe"),
                Path.Combine(mo2Dir, "mods", MODS_FOLDER, "f4se_loader.exe"),
                Path.Combine(mo2Dir, "mods", MODS_FOLDER, "Root", "f4se_loader.exe"),
            };

            // Также ищем рекурсивно в папке mods (включая подпапку Root)
            string modsBase = Path.Combine(mo2Dir, "mods");
            if (Directory.Exists(modsBase))
            {
                try
                {
                    // Сначала проверяем корневые папки модов
                    foreach (string modDir in Directory.GetDirectories(modsBase))
                    {
                        string f4seCandidate = Path.Combine(modDir, "f4se_loader.exe");
                        if (File.Exists(f4seCandidate))
                        {
                            f4sePath = f4seCandidate;
                            break;
                        }
                        // Также проверяем подпапку Root\ внутри каждой папки мода
                        string rootCandidate = Path.Combine(modDir, "Root", "f4se_loader.exe");
                        if (File.Exists(rootCandidate))
                        {
                            f4sePath = rootCandidate;
                            break;
                        }
                    }
                }
                catch { }
            }

            // Если не нашли в mods, проверяем основные пути
            if (string.IsNullOrEmpty(f4sePath))
            {
                foreach (var sp in searchPaths)
                {
                    if (!string.IsNullOrEmpty(sp) && File.Exists(sp))
                    {
                        f4sePath = sp;
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(f4sePath))
            {
                Log("ВНИМАНИЕ: f4se_loader.exe не найден. Запуск без F4SE.");
                return false;
            }

            Log($"F4SE найден: {f4sePath}");

            // Настраиваем MO2 для F4SE
            if (gamePath != null)
                EnsureF4seExecutableInMo2Ini(mo2Dir, f4sePath);

            string profilesDir = Path.Combine(mo2Dir, "profiles");
            string profilePath = Path.Combine(profilesDir, PROFILE_NAME);
            if (Directory.Exists(profilePath))
                EnsureF4seAsDefaultForProfile(profilePath, f4sePath);

            return true;
        }

        [Obsolete("Используйте PrepareBasicEnvironment + PrepareF4SEEnvironment")]
        private bool PrepareLaunchEnvironment(out string mo2Path, out string mo2Dir, out string f4sePath)
        {
            mo2Path = string.Empty;
            mo2Dir = string.Empty;
            f4sePath = string.Empty;
            return false;
        }

        private async void BtnLaunch_Click(object sender, RoutedEventArgs e)
        {
            btnLaunch.IsEnabled = false;
            btnMo2Only.IsEnabled = false;
            txtLog.Clear();

            try
            {
                if (!PrepareBasicEnvironment(out string mo2Path, out string mo2Dir))
                    return;

                // Ищем F4SE — если не нашли, не запускаем
                if (!PrepareF4SEEnvironment(out string f4sePath))
                {
                    ShowErrorAndExit("f4se_loader.exe не найден. Запуск невозможен без F4SE.");
                    return;
                }

                // Git-подход: -p "ProfileName" "f4sePath"
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

        private async void BtnLaunchMo2_Click(object sender, RoutedEventArgs e)
        {
            btnLaunch.IsEnabled = false;
            btnMo2Only.IsEnabled = false;
            txtLog.Clear();

            try
            {
                if (!PrepareBasicEnvironment(out string mo2Path, out string mo2Dir))
                    return;

                // Запуск только MO2 — без F4SE, только выбор профиля
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

        private void BtnMods_Click(object sender, RoutedEventArgs e)
        {
            string mo2Dir = AppDomain.CurrentDomain.BaseDirectory;
            string profilesDir = Path.Combine(mo2Dir, "profiles");
            string profilePath = Path.Combine(profilesDir, PROFILE_NAME);
            string modlistPath = Path.Combine(profilePath, "modlist.txt");

            if (!File.Exists(modlistPath))
            {
                Log($"Файл modlist.txt не найден: {modlistPath}");
                MessageBox.Show($"Файл модов не найден для профиля \"{PROFILE_NAME}\".\n" +
                                $"Запустите MO2 хотя бы один раз, чтобы создать modlist.txt.",
                    "Файл не найден", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Log("Открытие управления модами...");
            var modsWindow = new ModsWindow(mo2Dir, PROFILE_NAME, isDarkTheme)
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            modsWindow.ShowDialog();
            Log("Управление модами закрыто.");
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

        // ==================================================================
        // GAME PATH DETECTION (unchanged from original)
        // ==================================================================

        private string? FindGamePath()
        {
            // 1. Registry (64-bit)
            try
            {
                using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var subKey = key.OpenSubKey(@"SOFTWARE\Bethesda Softworks\Fallout4");
                if (subKey != null)
                {
                    var path = subKey.GetValue("Installed Path") as string;
                    if (!string.IsNullOrEmpty(path) && File.Exists(Path.Combine(path, "Fallout4.exe")))
                        return path;
                }
            }
            catch { }

            // 2. Registry (32-bit)
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

            // 3. Steam libraryfolders.vdf
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

            // 4. Manual folder selection
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

        // ==================================================================
        // SAVES SIZE
        // ==================================================================

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
    }
}
