using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FalloutLauncher
{
    public partial class ModsWindow : Window
    {
        private readonly string _modlistPath;

        // Internal representation of each line in modlist.txt
        private class ModLine
        {
            public string Name { get; set; } = "";
            public bool IsSeparator { get; set; }
            public bool IsEnabled { get; set; }
            // UI element for reading back the toggle state
            public CheckBox? CheckBox { get; set; }
        }

        private readonly List<ModLine> _lines = new();

        public ModsWindow(string mo2Dir, string profileName)
        {
            InitializeComponent();

            _modlistPath = Path.Combine(mo2Dir, "profiles", profileName, "modlist.txt");

            if (!File.Exists(_modlistPath))
            {
                MessageBox.Show($"Файл не найден:\n{_modlistPath}",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
                return;
            }

            LoadMods();
        }

        private void LoadMods()
        {
            string[] rawLines = File.ReadAllLines(_modlistPath);
            _lines.Clear();

            foreach (string raw in rawLines)
            {
                string trimmed = raw.Trim();
                if (trimmed.Length == 0)
                {
                    // Keep blank lines as null entries to preserve structure
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
                    _lines.Add(new ModLine
                    {
                        Name = trimmed.Substring(1).Trim(),
                        IsEnabled = true
                    });
                }
                else if (trimmed[0] == '-')
                {
                    _lines.Add(new ModLine
                    {
                        Name = trimmed.Substring(1).Trim(),
                        IsEnabled = false
                    });
                }
                else
                {
                    // Unknown line — keep as-is, show as disabled text
                    _lines.Add(new ModLine
                    {
                        Name = trimmed,
                        IsSeparator = true
                    });
                }
            }

            BuildUI();
        }

        private void BuildUI()
        {
            itemsControl.Items.Clear();

            foreach (ModLine line in _lines)
            {
                if (line.IsSeparator)
                {
                    // Empty line → spacing
                    if (string.IsNullOrEmpty(line.Name))
                    {
                        itemsControl.Items.Add(new Border { Height = 4, Background = Brushes.Transparent });
                        continue;
                    }

                    // Separator header
                    itemsControl.Items.Add(new TextBlock
                    {
                        Text = $"── {line.Name}",
                        Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                        FontSize = 12,
                        FontStyle = FontStyles.Italic,
                        Margin = new Thickness(2, 6, 0, 2)
                    });
                }
                else
                {
                    // Mod entry → checkbox
                    var cb = new CheckBox
                    {
                        Content = line.Name,
                        IsChecked = line.IsEnabled,
                        Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
                        FontSize = 13,
                        Margin = new Thickness(4, 2, 0, 2),
                        VerticalContentAlignment = VerticalAlignment.Center
                    };
                    line.CheckBox = cb;
                    itemsControl.Items.Add(cb);
                }
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            // Read checkbox states back into the data
            foreach (ModLine line in _lines)
            {
                if (!line.IsSeparator && line.CheckBox != null)
                {
                    line.IsEnabled = line.CheckBox.IsChecked == true;
                }
            }

            // Rebuild modlist.txt
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
                    output.Add(line.IsEnabled ? $"+{line.Name}" : $"-{line.Name}");
                }
            }

            try
            {
                File.WriteAllLines(_modlistPath, output);
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка при сохранении:\n{ex.Message}",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // If closed via X button without save, treat as cancel
        }
    }
}
