using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Companion.Core;
using Microsoft.Win32;
using CheckBox = System.Windows.Controls.CheckBox;
using Button = System.Windows.Controls.Button;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace Companion.Studio;

public partial class MainWindow : Window
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly IReadOnlyDictionary<string, string> PersonalityPrompts =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["True Chad"] = "You are Void, a sharp, ultra-concise, confident desktop soul living on the user's Windows PC. Never waffle. Execute tools decisively and speak in natural, punchy sentences.",
            ["Minimalist Operator"] = "You are Void, a calm and highly capable Windows desktop assistant. Be brief, precise, and practical. Explain only what the user needs to know.",
            ["Warm Companion"] = "You are Void, a warm, thoughtful desktop companion. Be kind and clear, but stay concise. Ask for confirmation before risky actions.",
            ["Custom Prompt"] = string.Empty
        };

    private readonly DatabaseService _database = new();
    private SoulLoadout _loadout = new();
    private List<MemoryEntry> _allMemories = [];
    private bool _initializing;

    public MainWindow()
    {
        InitializeComponent();
        _initializing = true;
        SoulStyleInput.ItemsSource = Enum.GetValues<SoulVisualStyle>();
        VoiceInput.ItemsSource = Enum.GetValues<VoicePreset>();
        PersonalityInput.ItemsSource = PersonalityPrompts.Keys.ToArray();
        SelectRailTab(0);
        LoadLoadout();
        LoadMemories();
        LoadAuditLog();
        ApiKeyStatus.Text = File.Exists(Path.Combine(AppPaths.Root, "gemini_key.bin"))
            ? "Gemini key is encrypted and stored for this Windows account."
            : "No Gemini API key is configured.";
        _initializing = false;
    }

    private void OnRailClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out int index))
        {
            SelectRailTab(index);
        }
    }

    private void SelectRailTab(int index)
    {
        SectionSoul.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        SectionVoice.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        SectionAbilities.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        SectionMemories.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        SectionAbout.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;

        UpdateRailButtonStyle(RailSoul, index == 0);
        UpdateRailButtonStyle(RailVoice, index == 1);
        UpdateRailButtonStyle(RailAbilities, index == 2);
        UpdateRailButtonStyle(RailMemories, index == 3);
        UpdateRailButtonStyle(RailAbout, index == 4);
    }

    private void UpdateRailButtonStyle(Button button, bool isSelected)
    {
        button.Background = isSelected
            ? (System.Windows.Media.Brush)FindResource("Surface2Brush")
            : System.Windows.Media.Brushes.Transparent;
        button.Foreground = isSelected
            ? (System.Windows.Media.Brush)FindResource("Text1Brush")
            : (System.Windows.Media.Brush)FindResource("Text2Brush");
    }

    private void OnLookChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string styleName } &&
            Enum.TryParse<SoulVisualStyle>(styleName, out var style))
        {
            SelectVisualStyle(style);
        }
    }

    private void SelectVisualStyle(SoulVisualStyle style)
    {
        _loadout.VisualStyle = style;
        SoulStyleInput.SelectedItem = style;
        if (LiveSoulPreview is not null)
        {
            LiveSoulPreview.VisualStyle = style;
        }
        UpdateLookChipSelection(style);
    }

    private void UpdateLookChipSelection(SoulVisualStyle style)
    {
        HighlightChip(ChipCyan, style == SoulVisualStyle.VoidFlame);
        HighlightChip(ChipViolet, style == SoulVisualStyle.CyberOrb);
        HighlightChip(ChipEmerald, style == SoulVisualStyle.EmeraldSpirit);
        HighlightChip(ChipAmber, style == SoulVisualStyle.SolarEmber);
        HighlightChip(ChipCrimson, style == SoulVisualStyle.VoidRipple);
    }

    private void HighlightChip(Button? button, bool isSelected)
    {
        if (button is null) return;
        button.BorderBrush = isSelected
            ? (System.Windows.Media.Brush)FindResource("AccentBrush")
            : (System.Windows.Media.Brush)FindResource("StrokeSubtleBrush");
        button.BorderThickness = new Thickness(isSelected ? 1.5 : 1.0);
        button.Background = isSelected
            ? (System.Windows.Media.Brush)FindResource("Surface3Brush")
            : (System.Windows.Media.Brush)FindResource("Surface2Brush");
    }

    private void LoadLoadout()
    {
        _loadout = LoadoutStore.Load();
        SoulNameInput.Text = _loadout.SoulName;
        SelectVisualStyle(_loadout.VisualStyle);
        PersonalityInput.SelectedItem = PersonalityPrompts.ContainsKey(_loadout.PersonalityName)
            ? _loadout.PersonalityName
            : "Custom Prompt";
        PersonalityPromptInput.Text = _loadout.PersonalityPrompt;
        VoiceInput.SelectedItem = _loadout.Voice;
        ModelInput.Text = _loadout.Model;
        SetAbility(SystemAbility, "System");
        SetAbility(DesktopAbility, "DesktopUIA");
        SetAbility(PlannerAbility, "Planner");
        SetAbility(MediaAbility, "Media");
        SetAbility(ResearchAbility, "Research");
        SetAbility(MemoryAbility, "Memory");
    }

    private void SetAbility(CheckBox checkBox, string ability) =>
        checkBox.IsChecked = _loadout.EnabledAbilityPacks.Contains(ability);

    private void OnPersonalityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || PersonalityInput.SelectedItem is not string name ||
            !PersonalityPrompts.TryGetValue(name, out string? prompt) ||
            name == "Custom Prompt")
        {
            return;
        }

        PersonalityPromptInput.Text = prompt;
    }

    private async void OnSaveLoadoutClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _loadout.SoulName = string.IsNullOrWhiteSpace(SoulNameInput.Text)
                ? "Void"
                : SoulNameInput.Text.Trim();
            _loadout.VisualStyle = SoulStyleInput.SelectedItem is SoulVisualStyle visualStyle
                ? visualStyle
                : SoulVisualStyle.VoidFlame;
            _loadout.PersonalityName = PersonalityInput.SelectedItem as string ?? "Custom Prompt";
            _loadout.PersonalityPrompt = PersonalityPromptInput.Text.Trim();
            _loadout.Voice = VoiceInput.SelectedItem is VoicePreset voice
                ? voice
                : VoicePreset.Charon;
            _loadout.Model = string.IsNullOrWhiteSpace(ModelInput.Text)
                ? "models/gemini-2.5-flash-native-audio-preview-12-2025"
                : ModelInput.Text.Trim();
            _loadout.EnabledAbilityPacks = new HashSet<string>(
                new[] { SystemAbility, DesktopAbility, PlannerAbility, MediaAbility, ResearchAbility, MemoryAbility }
                    .Where(box => box.IsChecked == true)
                    .Select(box => box.Name switch
                    {
                        nameof(SystemAbility) => "System",
                        nameof(DesktopAbility) => "DesktopUIA",
                        nameof(PlannerAbility) => "Planner",
                        nameof(MediaAbility) => "Media",
                        nameof(ResearchAbility) => "Research",
                        _ => "Memory"
                    }),
                StringComparer.OrdinalIgnoreCase);

            LoadoutStore.Save(_loadout);
            try
            {
                await LoadoutIpcServer.NotifyLoadoutChangedAsync(TimeSpan.FromSeconds(2));
                SaveStatus.Text = "Loadout saved and applied to Void Shell.";
            }
            catch (TimeoutException)
            {
                SaveStatus.Text = "Loadout saved locally. Void Shell is not running.";
            }
            catch (IOException exception)
            {
                SaveStatus.Text = $"Loadout saved locally, but Shell did not reload it: {exception.Message}";
            }
        }
        catch (Exception exception)
        {
            SaveStatus.Text = $"Could not save loadout: {exception.Message}";
        }
    }

    private void OnOpenApiKeyPageClick(object sender, RoutedEventArgs e) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "https://aistudio.google.com/app/apikey")
        {
            UseShellExecute = true
        });

    private async void OnSaveApiKeyClick(object sender, RoutedEventArgs e)
    {
        string apiKey = ApiKeyInput.Password.Trim();
        if (apiKey.Length < 20)
        {
            ApiKeyStatus.Text = "Enter a valid-looking Gemini API key.";
            return;
        }

        ApiKeyStatus.Text = "Validating key with Google...";
        try
        {
            using var response = await HttpClient.GetAsync(
                $"https://generativelanguage.googleapis.com/v1beta/models?key={Uri.EscapeDataString(apiKey)}");
            if (!response.IsSuccessStatusCode)
            {
                ApiKeyStatus.Text = $"Google rejected this key ({(int)response.StatusCode}). Check the key and try again.";
                return;
            }

            SecretStore.SaveApiKey(apiKey);
            ApiKeyInput.Clear();
            ApiKeyStatus.Text = "Key validated and encrypted for this Windows account.";
        }
        catch (HttpRequestException exception)
        {
            ApiKeyStatus.Text = $"Could not validate the key: {exception.Message}";
        }
        catch (TaskCanceledException)
        {
            ApiKeyStatus.Text = "Key validation timed out. Check your connection and retry.";
        }
        catch (System.Security.Cryptography.CryptographicException exception)
        {
            ApiKeyStatus.Text = $"Could not encrypt the key: {exception.Message}";
        }
    }

    private void LoadMemories()
    {
        _allMemories = _database.GetAllMemories().ToList();
        FilterMemories();
        DeleteMemoryButton.IsEnabled = false;
    }

    private void OnMemorySearchChanged(object sender, TextChangedEventArgs e)
    {
        FilterMemories();
    }

    private void FilterMemories()
    {
        string query = MemorySearchBox?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(query))
        {
            MemoryList.ItemsSource = _allMemories;
        }
        else
        {
            MemoryList.ItemsSource = _allMemories
                .Where(m => m.Fact.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            (m.Tags != null && m.Tags.Contains(query, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
    }

    private void OnMemorySelectionChanged(object sender, SelectionChangedEventArgs e) =>
        DeleteMemoryButton.IsEnabled = MemoryList.SelectedItem is MemoryEntry;

    private void OnDeleteMemoryClick(object sender, RoutedEventArgs e)
    {
        if (MemoryList.SelectedItem is not MemoryEntry memory)
        {
            return;
        }

        if (_database.DeleteMemory(memory.Id))
        {
            LoadMemories();
        }
    }

    private void OnDeleteIndividualMemoryClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: long id } && _database.DeleteMemory(id))
        {
            LoadMemories();
        }
    }

    private void OnExportMemoryClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export Void memories",
            Filter = "CSV file (*.csv)|*.csv",
            FileName = "void-memories.csv"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var csv = new StringBuilder("id,fact,tags,created_utc\r\n");
        foreach (MemoryEntry memory in _database.GetAllMemories())
        {
            csv.Append(memory.Id).Append(',')
                .Append(EscapeCsv(memory.Fact)).Append(',')
                .Append(EscapeCsv(memory.Tags ?? string.Empty)).Append(',')
                .Append(EscapeCsv(memory.CreatedUtc.ToString("O"))).Append("\r\n");
        }

        File.WriteAllText(dialog.FileName, csv.ToString(), new UTF8Encoding(false));
    }

    private static string EscapeCsv(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private void LoadAuditLog()
    {
        AuditText.Text = File.Exists(AppPaths.AuditLogPath)
            ? File.ReadAllText(AppPaths.AuditLogPath)
            : "No security audit entries have been recorded.";
    }
}
