using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FalloutLauncher
{
    public partial class ModsPanel : UserControl
    {
        private readonly List<ModLine> _lines = new();
        private string _modlistPath = "";
        private string _mo2Dir = "";
        private string _profileName = "";
        private Dictionary<int, string> _categories = new();
        private bool _useCategories = false;
        private Mo2BridgeClient? _bridge;

        // Internal representation of each line in modlist.txt
        private class ModLine
        {
            public string Name { get; set; } = "";      // display name (without [O])
            public string RawName { get; set; } = "";   // original name from modlist.txt (with [O] if present)
            public bool IsSeparator { get; set; }
            public bool IsEnabled { get; set; }
            public bool IsOptional { get; set; }
            public int CategoryId { get; set; } = 0;
            public CheckBox? CheckBox { get; set; }
        }

        public event EventHandler? CloseRequested;

        /// <summary>
        /// Calculates the panel width needed to fit the longest optional mod name.
        /// Range: 480–800px with padding.
        /// </summary>
        public double GetOptimalWidth()
        {
            double maxWidth = 0;
            var typeface = new Typeface(
                TryFindResource("FontPrimary") as FontFamily ?? new FontFamily("Segoe UI"),
                FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

            foreach (var line in _lines)
            {
                if (line.IsSeparator || !line.IsOptional) continue;

                var ft = new FormattedText(
                    line.Name,
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    13, // font size matches CheckBox FontSize
                    Brushes.Black,
                    96.0); // WPF default DPI

                if (ft.Width > maxWidth)
                    maxWidth = ft.Width;
            }

            // base 480 + text width + checkbox (24) + margins (32)
            double total = 480 + Math.Max(0, maxWidth - 350);
            if (total < 480) total = 480;
            if (total > 800) total = 800;
            return total;
        }

        public ModsPanel()
        {
            InitializeComponent();
        }

        public void LoadData(string mo2Dir, string profileName, Mo2BridgeClient? bridge = null)
        {
            _mo2Dir = mo2Dir;
            _profileName = profileName;
            _bridge = bridge;
            _modlistPath = Path.Combine(mo2Dir, "profiles", profileName, "modlist.txt");

            if (!File.Exists(_modlistPath))
            {
                MessageBox.Show($"Файл не найден:\n{_modlistPath}",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                CloseRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            // Load categories from MO2 (may not have real category data)
            _categories = LoadCategories();

            // Load mods: this populates _lines with names + reads meta.ini for CategoryId
            LoadModsData();

            // Ensure plugins.reference.txt exists (eternal plugin order snapshot)
            EnsurePluginsReference();

            // Use categories only if ≥30% of mods actually have categories assigned
            int totalMods = _lines.Count(l => !l.IsSeparator);
            int catsAssigned = _lines.Count(l => !l.IsSeparator && l.CategoryId > 0);
            _useCategories = _categories.Count > 1 && totalMods > 0 && (double)catsAssigned / totalMods >= 0.3;

            BuildUI();
        }

        private Dictionary<int, string> LoadCategories()
        {
            var result = new Dictionary<int, string> { [0] = "Unassigned" };
            try
            {
                string catPath = Path.Combine(_mo2Dir, "categories.dat");
                if (!File.Exists(catPath)) return result;

                int currentId = 0;
                foreach (string line in File.ReadAllLines(catPath))
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                    {
                        if (int.TryParse(trimmed.Trim('[', ']'), out int id))
                            currentId = id;
                    }
                    else if (trimmed.StartsWith("name=", StringComparison.OrdinalIgnoreCase))
                    {
                        result[currentId] = trimmed.Substring(5).Trim();
                    }
                }
            }
            catch { }
            return result;
        }

        private int GetModCategory(string modName)
        {
            try
            {
                string metaPath = Path.Combine(_mo2Dir, "mods", modName, "meta.ini");
                if (!File.Exists(metaPath)) return 0;

                foreach (string line in File.ReadAllLines(metaPath))
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("category=", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(trimmed.Substring(9).Trim(), out int id))
                            return id;
                    }
                }
            }
            catch { }
            return 0;
        }

        private void LoadModsData()
        {
            string[] rawLines = File.ReadAllLines(_modlistPath);
            _lines.Clear();

            foreach (string raw in rawLines)
            {
                string trimmed = raw.Trim();
                if (trimmed.Length == 0)
                {
                    _lines.Add(new ModLine { Name = "", IsSeparator = true });
                    continue;
                }

                if (trimmed[0] == '#')
                {
                    _lines.Add(new ModLine
                    {
                        Name = trimmed.TrimStart('#').Trim(),
                        IsSeparator = true
                    });
                }
                else if (trimmed[0] == '+')
                {
                    string rawName = trimmed.Substring(1).Trim();
                    string cleanName = rawName.Replace("[O]", "").Trim();
                    bool isOptional = rawName.Contains("[O]");
                    _lines.Add(new ModLine
                    {
                        Name = cleanName,
                        RawName = rawName,
                        IsEnabled = true,
                        IsOptional = isOptional,
                        CategoryId = GetModCategory(cleanName) // always check meta.ini
                    });
                }
                else if (trimmed[0] == '-')
                {
                    string rawName = trimmed.Substring(1).Trim();
                    string cleanName = rawName.Replace("[O]", "").Trim();
                    bool isOptional = rawName.Contains("[O]");
                    _lines.Add(new ModLine
                    {
                        Name = cleanName,
                        RawName = rawName,
                        IsEnabled = false,
                        IsOptional = isOptional,
                        CategoryId = GetModCategory(cleanName) // always check meta.ini
                    });
                }
                else
                {
                    _lines.Add(new ModLine
                    {
                        Name = trimmed,
                        IsSeparator = true
                    });
                }
            }
            // Note: BuildUI() is called from LoadData() after deciding _useCategories
        }

        private void BuildUI()
        {
            itemsControl.Items.Clear();

            // Filter only optional mods (marked with [O])
            if (_useCategories)
            {
                BuildUICategorized();
            }
            else
            {
                BuildUIFlat();
            }
        }

        private void BuildUICategorized()
        {
            // Collect non-separator mods (preserve original order)
            var modLines = _lines.Where(l => !l.IsSeparator).ToList();

            // Group by category, preserving category order by ID
            var grouped = modLines
                .GroupBy(l => l.CategoryId)
                .OrderBy(g => g.Key)
                .ToList();

            foreach (var group in grouped)
            {
                string catName = _categories.TryGetValue(group.Key, out string? name) ? name : "Unassigned";

                // Category header
                itemsControl.Items.Add(new Border
                {
                    Height = 1,
                    Background = TryFindResource("BorderBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(0x30, 0x36, 0x3D)),
                    Margin = new Thickness(0, 4, 0, 2)
                });

                itemsControl.Items.Add(new TextBlock
                {
                    Text = $"  {catName}",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                    FontSize = 11,
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(2, 2, 0, 4)
                });

                // Mods in this category
                foreach (var line in group)
                {
                    var cb = new CheckBox
                    {
                        Content = line.Name,
                        IsChecked = line.IsEnabled,
                        FontFamily = TryFindResource("FontPrimary") as FontFamily ?? new FontFamily("Segoe UI"),
                        Foreground = TryFindResource("TextPrimaryBrush") as Brush ?? Brushes.White,
                        FontSize = 13,
                        Margin = new Thickness(8, 2, 0, 2),
                        VerticalContentAlignment = VerticalAlignment.Center
                    };
                    line.CheckBox = cb;
                    itemsControl.Items.Add(cb);
                }
            }
        }

        private void BuildUIFlat()
        {
            bool hasOptional = false;
            foreach (ModLine line in _lines)
            {
                if (line.IsSeparator) continue;
                if (!line.IsOptional) continue;
                hasOptional = true;

                var cb = new CheckBox
                {
                    Content = line.Name,
                    IsChecked = line.IsEnabled,
                    FontFamily = TryFindResource("FontPrimary") as FontFamily ?? new FontFamily("Segoe UI"),
                    Foreground = TryFindResource("TextPrimaryBrush") as Brush ?? Brushes.White,
                    FontSize = 13,
                    Margin = new Thickness(4, 2, 0, 2),
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                line.CheckBox = cb;
                itemsControl.Items.Add(cb);
            }

            if (!hasOptional)
            {
                itemsControl.Items.Add(new TextBlock
                {
                    Text = "Нет опциональных модов с тегом [O]",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                    FontSize = 13,
                    FontStyle = FontStyles.Italic,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 40, 0, 0)
                });
            }
        }

        private string GetPluginsRefPath() =>
            Path.Combine(_mo2Dir, "profiles", _profileName, "plugins.reference.txt");

        private string GetPluginsPath() =>
            Path.Combine(_mo2Dir, "profiles", _profileName, "plugins.txt");

        /// <summary>
        /// Creates plugins.reference.txt (eternal plugin order) from current plugins.txt if missing.
        /// </summary>
        private void EnsurePluginsReference()
        {
            string refPath = GetPluginsRefPath();
            if (File.Exists(refPath)) return;

            string pluginsPath = GetPluginsPath();
            if (File.Exists(pluginsPath))
            {
                File.Copy(pluginsPath, refPath, overwrite: false);
            }
        }

        /// <summary>
        /// Rebuilds plugins.txt from the reference file, applying * prefixes for changed mods.
        /// This preserves the eternal plugin order because MO2 sees a CONSISTENT plugins.txt + modlist.txt.
        /// </summary>
        private void SyncPluginsWithReference(List<ModLine> changedMods)
        {
            try
            {
                string refPath = GetPluginsRefPath();
                string pluginsPath = GetPluginsPath();
                if (!File.Exists(refPath)) return;

                // Read reference file
                var refLines = new List<string>(File.ReadAllLines(refPath));

                foreach (var mod in changedMods)
                {
                    // Find mod folder — try RawName first, then clean Name
                    string modFolder = Path.Combine(_mo2Dir, "mods", mod.RawName);
                    if (!Directory.Exists(modFolder))
                        modFolder = Path.Combine(_mo2Dir, "mods", mod.Name);
                    if (!Directory.Exists(modFolder)) continue;

                    // Scan for plugin files (.esp, .esl, .esm)
                    var plugins = new List<string>();
                    foreach (string file in Directory.GetFiles(modFolder, "*.*", SearchOption.AllDirectories))
                    {
                        string ext = Path.GetExtension(file).ToLowerInvariant();
                        if (ext == ".esp" || ext == ".esl" || ext == ".esm")
                            plugins.Add(Path.GetFileName(file));
                    }

                    if (plugins.Count == 0) continue;

                    foreach (string plugin in plugins)
                    {
                        int index = -1;
                        for (int i = 0; i < refLines.Count; i++)
                        {
                            string line = refLines[i].Trim();
                            if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;

                            string cleanPlugin = line.StartsWith("*") ? line.Substring(1) : line;
                            if (string.Equals(cleanPlugin, plugin, StringComparison.OrdinalIgnoreCase))
                            {
                                index = i;
                                break;
                            }
                        }

                        if (index >= 0)
                        {
                            // Update * prefix based on mod state
                            refLines[index] = mod.IsEnabled ? $"*{plugin}" : plugin;
                        }
                        else if (mod.IsEnabled)
                        {
                            // New plugin — add to reference too
                            refLines.Add($"*{plugin}");
                        }
                    }
                }

                // Completely overwrite plugins.txt with the updated reference
                File.WriteAllLines(pluginsPath, refLines);
            }
            catch { }
        }

        private async void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            // Read checkbox states and track which mods changed
            var changedMods = new List<ModLine>();
            foreach (ModLine line in _lines)
            {
                if (!line.IsSeparator && line.CheckBox != null)
                {
                    bool newState = line.CheckBox.IsChecked == true;
                    if (newState != line.IsEnabled)
                    {
                        line.IsEnabled = newState;
                        changedMods.Add(line);
                    }
                }
            }

            if (changedMods.Count == 0)
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            // Если bridge доступен — сохраняем через MO2 API (рекомендуемый путь)
            if (_bridge != null && _bridge.Connected)
            {
                var errors = new List<string>();
                foreach (var mod in changedMods)
                {
                    try
                    {
                        await _bridge.SetModActiveAsync(mod.Name, mod.IsEnabled);
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"'{mod.Name}': {ex.Message}");
                    }
                }
                if (errors.Count > 0)
                {
                    MessageBox.Show(
                        $"Не удалось изменить {errors.Count} модов:\n\n{string.Join("\n", errors.Take(5))}"
                        + (errors.Count > 5 ? $"\n...и ещё {errors.Count - 5}" : ""),
                        "Ошибка сохранения через Bridge",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            else
            {
                // Fallback: прямая запись modlist.txt (как было раньше)
                var output = new List<string>();
                foreach (ModLine line in _lines)
                {
                    if (line.IsSeparator)
                    {
                        if (string.IsNullOrEmpty(line.Name))
                            output.Add("");
                        else
                            output.Add($"#{line.Name}");
                    }
                    else
                    {
                        string saveName = !string.IsNullOrEmpty(line.RawName) ? line.RawName : line.Name;
                        output.Add(line.IsEnabled ? $"+{saveName}" : $"-{saveName}");
                    }
                }

                try
                {
                    File.WriteAllLines(_modlistPath, output);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Ошибка при сохранении:\n{ex.Message}",
                        "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Sync plugins.txt from reference file (preserves eternal plugin order)
                if (changedMods.Count > 0)
                    SyncPluginsWithReference(changedMods);
            }

            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
