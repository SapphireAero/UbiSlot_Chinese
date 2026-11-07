using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UbiSlot.Core;
using UbiSlot.Games;
using UbiSlot.Ubisoft;

using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfImage = System.Windows.Controls.Image;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;

namespace UbiSlot;

public enum AchievementSortMode
{
    ID,
    Name,
    DateAchieved,
    Locked,
    Unlocked
}

public partial class AchievementWindow : Window
{
    private string _spoolFile;
    private string _gameId;

    private readonly GameVariantResolver _variantResolver;

    private readonly SpoolManager _spoolManager;
    private readonly AchievementCache _achievementCache;
    private readonly GameDatabase _gameDatabase;

    private readonly List<AchievementDefinition> _achievements = [];

    private readonly List<WpfCheckBox> _selectionBoxes = [];

    private readonly Dictionary<
        AchievementDefinition,
        Border> _achievementCards = [];

    private UbiSlotSettings _settings;
    private byte[]? _originalSpoolData;
    private bool _originalSpoolExisted;
    private bool _ubislotCreatedSpool;
    private bool _hasSessionChanges;

    private bool _gameReady;
    private bool _sessionClosing;
    private bool _allowClose;

    private readonly List<AchievementDefinition> _defaultAchievementOrder = [];
    private bool _defaultOrderCaptured;

    private AchievementSortMode _sortMode =
        AchievementSortMode.ID;

    public AchievementWindow(
        string spoolFile,
        string gameId)
    {
        InitializeComponent();

        _settings =
            UbiSlotSettings.Load();

        ApplyWindowTheme(
            _settings.IsDarkTheme);

        _spoolFile = spoolFile;
        _gameId = gameId;

        _spoolManager = new SpoolManager();
        _achievementCache = new AchievementCache();
        _gameDatabase = new GameDatabase();
        _variantResolver = new GameVariantResolver(_gameDatabase);

        Loaded += AchievementWindow_Loaded;
        Closing += AchievementWindow_Closing;
    }

    private async void AchievementWindow_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            Loaded -= AchievementWindow_Loaded;

            if (!uint.TryParse(
                    _gameId,
                    out uint gameId))
            {
                throw new InvalidOperationException(
                    $"Invalid Ubisoft game ID: {_gameId}");
            }

            string gameName =
                _gameDatabase.GetGameName(_gameId)
                ?? $"Game {_gameId}";

            GameTitleText.Text = gameName;
            ProgressText.Text =
                "正在连接育碧游戏会话...";

            AddLoadingMessage(
                "正在加载成就...");

            Console.WriteLine(
                $"[AchievementWindow] Opening for " +
                $"{gameName} ({gameId}).");

            Console.WriteLine(
                "[AchievementWindow] " +
                "UPLAY_Startup() will NOT be called.");

            uint? runningGameId =
                UbisoftGameLauncher.GetRunningUbisoftGameId();

            if (!runningGameId.HasValue)
            {
                throw new InvalidOperationException(
                    "育碧游戏会话已不再运行。");
            }

            if (runningGameId.Value != gameId)
            {
                if (!_variantResolver.IsSameGame(runningGameId.Value, gameId))
                {
                    string runningGameName =
                        _gameDatabase.GetGameName(runningGameId.Value.ToString())
                        ?? $"Game {runningGameId.Value}";

                    throw new InvalidOperationException(
                        $"{runningGameName} is running instead of {gameName}.");
                }

                _gameId = runningGameId.Value.ToString();
            }

            // FIND SPOOL

            await RefreshSpoolFileAsync();

            Console.WriteLine(
                "[AchievementWindow] " +
                $"Spool: {(_spoolFile.Length > 0 ? _spoolFile : "NONE")}");

            CaptureOriginalSpoolState();

            if (File.Exists(_spoolFile))
            {
                LoadAchievements();
            }
            else
            {
                LoadAchievementsWithoutSpool();
            }

            CaptureDefaultAchievementOrder();

            _gameReady = true;

            ProgressText.Text =
                "就绪";

            UpdateSelection();
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[AchievementWindow] Startup error: {ex}");

            Dialogue.ShowError(
                this,
                "UbiSlot",
                "UbiSlot 无法打开该游戏的成就。\n\n" +
                ex.Message);

            _allowClose = true;
            Close();
        }
    }

    private async Task RefreshSpoolFileAsync()
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            RefreshSpoolFileOnce();

            if (!string.IsNullOrWhiteSpace(_spoolFile) &&
                File.Exists(_spoolFile))
            {
                return;
            }

            await Task.Delay(500);
        }

        RefreshSpoolFileOnce();

        if (string.IsNullOrWhiteSpace(_spoolFile))
        {
            _spoolFile =
                Path.Combine(
                    _spoolManager.GetUserSpoolDirectory(),
                    $"{_gameId}.spool");

            Console.WriteLine(
                $"[AchievementWindow] No existing spool. " +
                $"Using target spool: {_spoolFile}");
        }
    }

    private void RefreshSpoolFileOnce()
    {
        try
        {
            List<string> spoolFiles =
                _spoolManager.FindSpoolFiles();

            string? exactMatch =
                spoolFiles.FirstOrDefault(
                    spoolFile =>
                        string.Equals(
                            _spoolManager.GetGameId(spoolFile),
                            _gameId,
                            StringComparison.OrdinalIgnoreCase));

            if (exactMatch == null && uint.TryParse(_gameId, out uint requestedId))
            {
                exactMatch = spoolFiles.FirstOrDefault(file =>
                    uint.TryParse(_spoolManager.GetGameId(file), out uint spoolId) &&
                    _variantResolver.IsSameGame(spoolId, requestedId));

                if (exactMatch != null)
                {
                    _gameId = _spoolManager.GetGameId(exactMatch);
                }
            }

            if (!string.IsNullOrWhiteSpace(exactMatch) &&
                File.Exists(exactMatch))
            {
                _spoolFile = exactMatch;

                Console.WriteLine(
                    $"[AchievementWindow] Exact spool match: {_spoolFile}");

                return;
            }

            if (!string.IsNullOrWhiteSpace(_spoolFile) &&
                File.Exists(_spoolFile))
            {
                Console.WriteLine(
                    $"[AchievementWindow] Existing spool retained: {_spoolFile}");

                return;
            }

            _spoolFile = string.Empty;
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[AchievementWindow] Spool refresh error: {ex.Message}");

            if (string.IsNullOrWhiteSpace(_spoolFile))
            {
                _spoolFile = string.Empty;
            }
        }
    }

    private void AddLoadingMessage(
        string message)
    {
        AchievementList.Children.Clear();

        var text =
            new TextBlock
            {
                Text = message,

                FontSize = 16,

                Foreground =
                    GetThemeBrush(
                        "SecondaryText"),

                HorizontalAlignment =
                    WpfHorizontalAlignment.Center,

                VerticalAlignment =
                    VerticalAlignment.Center,

                TextAlignment =
                    TextAlignment.Center,

                Margin =
                    new Thickness(20)
            };

        AchievementList.Children.Add(text);
    }

    private void ReplaceLoadingMessage(
        string message)
    {
        AddLoadingMessage(message);
    }

    private void LoadAchievements()
    {
        try
        {
            if (!File.Exists(_spoolFile))
            {
                LoadAchievementsWithoutSpool();
                return;
            }

            List<SpoolRecord> spoolRecords =
                _spoolManager.GetRecords(
                    _spoolFile);

            _achievements.Clear();

            _achievements.AddRange(
                _achievementCache.GetAchievementsForGame(
                    _gameId,
                    spoolRecords));

            BuildAchievementList();
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[AchievementWindow] " +
                $"Achievement load error: {ex}");

            Dialogue.ShowError(
                this,
                "UbiSlot",
                "UbiSlot 无法加载该游戏的成就。");

            _allowClose = true;
            Close();
        }
    }

    private void LoadAchievementsWithoutSpool()
    {
        try
        {
            _achievements.Clear();

            _achievements.AddRange(
                _achievementCache.ReadAchievements(
                    _gameId));

            BuildAchievementList();
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[AchievementWindow] " +
                $"Achievement load error: {ex}");

            Dialogue.ShowError(
                this,
                "UbiSlot",
                "UbiSlot 无法加载该游戏的成就。");

            _allowClose = true;
            Close();
        }
    }

    private void BuildAchievementList()
    {
        int unlocked =
            _achievements.Count(
                achievement =>
                    achievement.IsUnlocked);

        string gameName =
            _gameDatabase.GetGameName(
                _gameId)
            ?? $"Game {_gameId}";

        GameTitleText.Text =
            gameName;

        ProgressText.Text =
            $"已解锁 {unlocked}/{_achievements.Count}";

        AchievementList.Children.Clear();

        _selectionBoxes.Clear();
        _achievementCards.Clear();

        foreach (
            AchievementDefinition achievement
            in _achievements)
        {
            AddAchievement(
                achievement);
        }

        UpdateAchievementList();
        UpdateSelection();
    }

    private void AddAchievement(
        AchievementDefinition achievement)
    {
        var card =
            new Border
            {
                Style =
                    (Style)FindResource(
                        "AchievementCardStyle")
            };

        var grid =
            new Grid();

        grid.ColumnDefinitions.Add(
            new ColumnDefinition
            {
                Width =
                    GridLength.Auto
            });

        grid.ColumnDefinitions.Add(
            new ColumnDefinition
            {
                Width =
                    new GridLength(
                        1,
                        GridUnitType.Star)
            });

        grid.ColumnDefinitions.Add(
            new ColumnDefinition
            {
                Width =
                    GridLength.Auto
            });

        var iconBorder =
            new Border
            {
                Width = 64,
                Height = 64,

                CornerRadius =
                    new CornerRadius(5),

                Background =
                    GetThemeBrush(
                        "FieldBackground")
            };

        if (!string.IsNullOrWhiteSpace(
                achievement.IconPath) &&
            File.Exists(
                achievement.IconPath))
        {
            var image =
                new WpfImage
                {
                    Stretch =
                        Stretch.Uniform
                };

            image.Source =
                new BitmapImage(
                    new Uri(
                        achievement.IconPath!,
                        UriKind.Absolute));

            iconBorder.Child =
                image;
        }
        else
        {
            iconBorder.Child =
                new TextBlock
                {
                    Text = "?",

                    FontSize = 24,

                    Foreground =
                        GetThemeBrush(
                            "MutedText"),

                    HorizontalAlignment =
                        WpfHorizontalAlignment.Center,

                    VerticalAlignment =
                        VerticalAlignment.Center
                };
        }

        Grid.SetColumn(
            iconBorder,
            0);

        grid.Children.Add(
            iconBorder);

        var textPanel =
            new StackPanel
            {
                Margin =
                    new Thickness(
                        16,
                        2,
                        16,
                        2)
            };

        textPanel.Children.Add(
            new TextBlock
            {
                Text =
                    achievement.Name,

                FontSize = 15,

                FontWeight =
                    FontWeights.SemiBold
            });

        textPanel.Children.Add(
            new TextBlock
            {
                Text =
                    achievement.Description,

                Margin =
                    new Thickness(
                        0,
                        5,
                        0,
                        0),

                Foreground =
                    GetThemeBrush(
                        "SecondaryText"),

                FontSize = 12,

                TextWrapping =
                    TextWrapping.Wrap
            });

        if (!achievement.IsUnlocked)
        {
            textPanel.Children.Add(
                new TextBlock
                {
                    Text = "未解锁",

                    Margin =
                        new Thickness(
                            0,
                            7,
                            0,
                            0),

                    Foreground =
                        GetThemeBrush(
                            "MutedText"),

                    FontSize = 11
                });
        }

        Grid.SetColumn(
            textPanel,
            1);

        grid.Children.Add(
            textPanel);

        _achievementCards[
            achievement] =
            card;

        if (achievement.IsUnlocked)
        {
            var unlockDateText =
                new TextBlock
                {
                    Text =
                        achievement.UnlockTimeUtc.HasValue
                            ? achievement.UnlockTimeUtc
                                .Value
                                .ToLocalTime()
                                .ToString(
                                    "dd MMM yyyy, HH:mm")
                            : string.Empty,

                    Foreground =
                        GetThemeBrush(
                            "SuccessText"),

                    FontSize = 15,

                    FontWeight =
                        FontWeights.SemiBold,

                    VerticalAlignment =
                        VerticalAlignment.Center,

                    HorizontalAlignment =
                        WpfHorizontalAlignment.Center
                };

            Grid.SetColumn(
                unlockDateText,
                2);

            grid.Children.Add(
                unlockDateText);
        }
        else
        {
            var checkBox =
                new WpfCheckBox
                {
                    Width = 28,
                    Height = 28,

                    RenderTransform =
                        new ScaleTransform(
                            1.8,
                            1.8),

                    RenderTransformOrigin =
                        new WpfPoint(
                            0.5,
                            0.5),

                    VerticalAlignment =
                        VerticalAlignment.Center,

                    HorizontalAlignment =
                        WpfHorizontalAlignment.Center
                };

            checkBox.Tag =
                achievement;

            checkBox.Checked +=
                SelectionChanged;

            checkBox.Unchecked +=
                SelectionChanged;

            _selectionBoxes.Add(
                checkBox);

            Grid.SetColumn(
                checkBox,
                2);

            grid.Children.Add(
                checkBox);
        }

        card.Child =
            grid;

        AchievementList.Children.Add(
            card);
    }

    private void SelectionChanged(
        object sender,
        RoutedEventArgs e)
    {
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        int selected =
            _selectionBoxes.Count(
                checkBox =>
                    checkBox.IsChecked == true);

        int locked =
            _selectionBoxes.Count(
                checkBox =>
                    checkBox.IsEnabled);

        int lockedSelected =
            _selectionBoxes.Count(
                checkBox =>
                    checkBox.IsEnabled &&
                    checkBox.IsChecked == true);

        SelectionText.Text =
            $"已选择 {selected} 项";

        GetAchievementButton.IsEnabled =
            selected > 0 &&
            _gameReady &&
            !_sessionClosing;

        if (locked > 0 &&
            lockedSelected == locked)
        {
            SelectAllLockedButton.Content =
                "取消全选";
        }
        else
        {
            SelectAllLockedButton.Content =
                "选择全部未解锁";
        }
    }

    private void SelectAllLockedButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!_gameReady ||
            _sessionClosing)
        {
            return;
        }

        int lockedSelected =
            _selectionBoxes.Count(
                checkBox =>
                    checkBox.IsEnabled &&
                    checkBox.IsChecked == true);

        int locked =
            _selectionBoxes.Count(
                checkBox =>
                    checkBox.IsEnabled);

        bool shouldDeselect =
            locked > 0 &&
            lockedSelected == locked;

        foreach (
            WpfCheckBox checkBox
            in _selectionBoxes)
        {
            if (!checkBox.IsEnabled)
            {
                continue;
            }

            checkBox.IsChecked =
                !shouldDeselect;
        }

        UpdateSelection();
    }

    private void SortButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!_gameReady ||
            _sessionClosing)
        {
            return;
        }

        SortPopup.IsOpen =
            !SortPopup.IsOpen;
    }

    private void SortOption_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not Button button ||
            string.IsNullOrWhiteSpace(
                button.Tag?.ToString()))
        {
            return;
        }

        if (!Enum.TryParse(
                button.Tag!.ToString(),
                true,
                out AchievementSortMode sortMode))
        {
            return;
        }

        _sortMode =
            sortMode;

        SortPopup.IsOpen =
            false;

        ApplyAchievementSort();
    }

    private void ApplyAchievementSort()
    {
        HashSet<uint> selectedAchievementIds =
            _selectionBoxes
                .Where(
                    checkBox =>
                        checkBox.IsChecked == true)
                .Select(
                    checkBox =>
                        ((AchievementDefinition)
                            checkBox.Tag!)
                            .AchievementId)
                .ToHashSet();

        IEnumerable<AchievementDefinition> sorted;

        switch (_sortMode)
        {
            case AchievementSortMode.Name:

                sorted =
                    _achievements
                        .OrderBy(
                            achievement =>
                                achievement.Name,
                            StringComparer.OrdinalIgnoreCase)
                        .ThenBy(
                            achievement =>
                                achievement.AchievementId);

                break;

            case AchievementSortMode.DateAchieved:

                sorted =
                    _achievements
                        .OrderBy(
                            achievement =>
                                achievement.UnlockTimeUtc ??
                                DateTime.MaxValue)
                        .ThenBy(
                            achievement =>
                                achievement.Name,
                            StringComparer.OrdinalIgnoreCase);

                break;

            case AchievementSortMode.Locked:

                sorted =
                    _achievements
                        .OrderByDescending(
                            achievement =>
                                !achievement.IsUnlocked)
                        .ThenBy(
                            achievement =>
                                achievement.Name,
                            StringComparer.OrdinalIgnoreCase);

                break;

            case AchievementSortMode.Unlocked:

                sorted =
                    _achievements
                        .OrderByDescending(
                            achievement =>
                                achievement.IsUnlocked)
                        .ThenBy(
                            achievement =>
                                achievement.Name,
                            StringComparer.OrdinalIgnoreCase);

                break;

            default:

                sorted =
                    _defaultAchievementOrder;

                break;
        }

        List<AchievementDefinition> newOrder =
            sorted.ToList();

        _achievements.Clear();

        _achievements.AddRange(
            newOrder);

        BuildAchievementList();

        foreach (
            WpfCheckBox checkBox
            in _selectionBoxes)
        {
            if (checkBox.Tag is AchievementDefinition achievement)
            {
                checkBox.IsChecked =
                    selectedAchievementIds.Contains(
                        achievement.AchievementId);
            }
        }

        UpdateSelection();
    }

    private void CaptureDefaultAchievementOrder()
    {
        if (_defaultOrderCaptured)
        {
            return;
        }

        _defaultAchievementOrder.Clear();

        _defaultAchievementOrder.AddRange(
            _achievements);

        _defaultOrderCaptured =
            true;
    }

    private void SearchButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!_gameReady ||
            _sessionClosing)
        {
            return;
        }

        if (SearchPopup.IsOpen)
        {
            SearchPopup.IsOpen = false;
            return;
        }

        SearchPopup.IsOpen = true;

        Dispatcher.BeginInvoke(
            new Action(
                () =>
                {
                    AchievementSearchBox.Focus();

                    Keyboard.Focus(
                        AchievementSearchBox);

                    AchievementSearchBox.SelectAll();
                }),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    private void SearchPopup_Closed(
        object? sender,
        EventArgs e)
    {
    }

    private void AchievementSearchBox_TextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        if (!_gameReady ||
            _sessionClosing)
        {
            return;
        }

        UpdateAchievementList();
    }

    private void UpdateAchievementList()
    {
        string search =
            AchievementSearchBox.Text.Trim();

        foreach (
            KeyValuePair<
                AchievementDefinition,
                Border> item
            in _achievementCards)
        {
            AchievementDefinition achievement =
                item.Key;

            bool visible =
                string.IsNullOrWhiteSpace(search)
                ||
                achievement.Name.Contains(
                    search,
                    StringComparison.OrdinalIgnoreCase)
                ||
                achievement.AchievementId
                    .ToString()
                    .Contains(
                        search,
                        StringComparison.OrdinalIgnoreCase);

            item.Value.Visibility =
                visible
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }
    }

    private void CaptureOriginalSpoolState()
    {
        _originalSpoolData = null;
        _originalSpoolExisted =
            !string.IsNullOrWhiteSpace(_spoolFile) &&
            File.Exists(_spoolFile);

        _ubislotCreatedSpool = false;
        _hasSessionChanges = false;

        if (!_originalSpoolExisted)
        {
            Console.WriteLine(
                "[AchievementWindow] " +
                "Original spool state: NO SPOOL.");

            return;
        }

        try
        {
            _originalSpoolData =
                File.ReadAllBytes(
                    _spoolFile);

            _spoolManager.ValidateSerializedRecords(
                _originalSpoolData);

            Console.WriteLine(
                "[AchievementWindow] " +
                $"Original spool state captured: {_spoolFile}");
        }
        catch (Exception ex)
        {
            _originalSpoolData = null;

            Console.WriteLine(
                "[AchievementWindow] " +
                $"Original spool snapshot failed: {ex.Message}");

            throw;
        }
    }

    private void WriteModifiedSpool(
        byte[] serializedData)
    {
        if (string.IsNullOrWhiteSpace(_spoolFile))
        {
            throw new InvalidOperationException(
                "找不到目标 spool 文件。");
        }

        if (File.Exists(_spoolFile))
        {
            _spoolManager.WriteSpoolFile(
                _spoolFile,
                serializedData);

            return;
        }

        string? directory =
            Path.GetDirectoryName(
                _spoolFile);

        if (!string.IsNullOrWhiteSpace(
                directory))
        {
            Directory.CreateDirectory(
                directory);
        }

        string temporaryFile =
            _spoolFile +
            ".ubislot.tmp";

        try
        {
            File.WriteAllBytes(
                temporaryFile,
                serializedData);

            byte[] writtenData =
                File.ReadAllBytes(
                    temporaryFile);

            _spoolManager.ValidateSerializedRecords(
                writtenData);

            File.Move(
                temporaryFile,
                _spoolFile,
                true);

            _ubislotCreatedSpool = true;
        }
        finally
        {
            if (File.Exists(
                    temporaryFile))
            {
                File.Delete(
                    temporaryFile);
            }
        }
    }

    private void RestoreSessionOriginalSpool()
    {
        if (string.IsNullOrWhiteSpace(
                _spoolFile))
        {
            throw new InvalidOperationException(
                "找不到目标 spool 文件。");
        }

        if (_originalSpoolExisted &&
            _originalSpoolData != null)
        {
            string? directory =
                Path.GetDirectoryName(
                    _spoolFile);

            if (!string.IsNullOrWhiteSpace(
                    directory))
            {
                Directory.CreateDirectory(
                    directory);
            }

            string temporaryFile =
                _spoolFile +
                ".ubislot.restore.tmp";

            try
            {
                File.WriteAllBytes(
                    temporaryFile,
                    _originalSpoolData);

                byte[] restoredData =
                    File.ReadAllBytes(
                        temporaryFile);

                _spoolManager.ValidateSerializedRecords(
                    restoredData);

                File.Move(
                    temporaryFile,
                    _spoolFile,
                    true);

                _ubislotCreatedSpool = false;
                _hasSessionChanges = false;

                Console.WriteLine(
                    "[AchievementWindow] " +
                    $"Restored original spool: {_spoolFile}");

                return;
            }
            finally
            {
                if (File.Exists(
                        temporaryFile))
                {
                    File.Delete(
                        temporaryFile);
                }
            }
        }

        if (_ubislotCreatedSpool &&
            File.Exists(_spoolFile))
        {
            File.Delete(
                _spoolFile);

            _ubislotCreatedSpool = false;
            _hasSessionChanges = false;

            Console.WriteLine(
                "[AchievementWindow] " +
                $"Removed UbiSlot-created spool: {_spoolFile}");

            return;
        }

        throw new InvalidOperationException(
            "没有需要恢复的 UbiSlot 更改。");
    }

    private void RestoreOriginalButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!_gameReady ||
            _sessionClosing)
        {
            return;
        }

        if (!_hasSessionChanges)
        {
            Dialogue.ShowInformation(
                this,
                "恢复原始数据",
                "没有需要恢复的成就更改。");

            return;
        }

        bool confirmed =
            Dialogue.ShowConfirmation(
                this,
                "恢复原始数据",
                "恢复打开游戏时的成就数据？\n\n" +
                "本次会话中 UbiSlot 所做的成就更改将被移除。\n\n" +
                "这无法重新锁定已经同步的成就。",
                "恢复",
                "取消",
                DialogueIconKind.Warning);

        if (!confirmed)
        {
            return;
        }

        try
        {
            RestoreSessionOriginalSpool();

            if (File.Exists(
                    _spoolFile))
            {
                LoadAchievements();
            }
            else
            {
                LoadAchievementsWithoutSpool();
            }

            AchievementSearchBox.Clear();
            SearchPopup.IsOpen = false;

            ProgressText.Text =
                "已恢复原始本地成就数据。";

            UpdateSelection();

            Dialogue.ShowInformation(
                this,
                "恢复原始数据",
                "原始本地成就数据已恢复。");
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                "[AchievementWindow] " +
                $"Restore error: {ex}");

            Dialogue.ShowError(
                this,
                "恢复原始数据",
                "UbiSlot 无法恢复原始成就数据。\n\n" +
                ex.Message);
        }
    }

    private void GetAchievementButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!_gameReady ||
            _sessionClosing)
        {
            return;
        }

        List<AchievementDefinition> selected =
            _selectionBoxes
                .Where(
                    checkBox =>
                        checkBox.IsChecked == true)
                .Select(
                    checkBox =>
                        (AchievementDefinition)
                        checkBox.Tag!)
                .ToList();

        if (selected.Count == 0)
        {
            return;
        }

        IEnumerable<string> displayedNames =
            selected
                .Take(5)
                .Select(
                    achievement =>
                        $"• {achievement.Name}");

        string names =
            string.Join(
                "\n",
                displayedNames);

        int remaining =
            selected.Count - 5;

        if (remaining > 0)
        {
            names +=
                $"\n(+{remaining})";
        }

        string message =
            "确定解锁以下成就吗："
            + "\n\n"
            + names
            + "\n\n"
            + "更改将立即写入。"
            + "\n"
            + "如果希望恢复原始数据，请保持此窗口打开。"
            + "\n\n"
            + "关闭窗口并结束育碧游戏会话后，"
            + "已同步的成就无法通过 UbiSlot 再次锁定。"
            + "\n"
            + "请避免一次解锁全部成就；此操作未经测试，可能带来额外风险。";

        bool confirmed =
            Dialogue.ShowConfirmation(
                this,
                "确认解锁成就",
                message,
                "解锁",
                "取消",
                DialogueIconKind.Warning);

        if (!confirmed)
        {
            return;
        }

        try
        {
            PrepareAchievementChanges(
                selected);
        }
        catch (UnauthorizedAccessException)
        {
            Dialogue.ShowError(
                this,
                "解锁成就",
                "UbiSlot 无法访问此游戏的数据。\n\n" +
                "请关闭游戏后重试。");
        }
        catch (IOException)
        {
            Dialogue.ShowError(
                this,
                "解锁成就",
                "UbiSlot 无法保存成就。\n\n" +
                "请关闭游戏后重试。");
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[AchievementWindow] Unlock error: {ex}");

            Dialogue.ShowError(
                this,
                "解锁成就",
                "准备成就数据时出错。\n\n" +
                "没有进行任何更改。");
        }
    }

    private void PrepareAchievementChanges(
        List<AchievementDefinition> selected)
    {
        List<SpoolRecord> existingRecords =
            File.Exists(_spoolFile)
                ? _spoolManager.GetRecords(
                    _spoolFile)
                : [];

        var existingIds =
            existingRecords
                .Select(
                    record =>
                        record.AchievementId)
                .ToHashSet();

        var newRecords =
            new List<SpoolRecord>();

        foreach (
            AchievementDefinition achievement
            in selected)
        {
            if (existingIds.Contains(
                    achievement.AchievementId))
            {
                continue;
            }

            SpoolRecord record =
                _spoolManager.CreateAchievementRecord(
                    achievement.AchievementId);

            newRecords.Add(
                record);
        }

        if (newRecords.Count == 0)
        {
            return;
        }

        List<SpoolRecord> finalRecords =
            _spoolManager.PrepareRecordsForWrite(
                existingRecords,
                newRecords);

        byte[] serializedData =
            _spoolManager.SerializeRecords(
                finalRecords);

        _spoolManager.ValidateSerializedRecords(
            serializedData);

        WriteModifiedSpool(
            serializedData);

        _hasSessionChanges = true;

        Console.WriteLine(
            "[AchievementWindow] " +
            $"Achievement changes written immediately: {_spoolFile}");

        Console.WriteLine(
            "[AchievementWindow] " +
            "Achievement window remains open.");

        AchievementSearchBox.Clear();
        SearchPopup.IsOpen = false;

        if (File.Exists(
                _spoolFile))
        {
            LoadAchievements();
        }
        else
        {
            LoadAchievementsWithoutSpool();
        }

        ProgressText.Text =
            "更改已保存在本地。保持此窗口打开即可恢复。";

        UpdateSelection();
    }

    private void AchievementWindow_Closing(
        object? sender,
        CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        _sessionClosing = true;
        e.Cancel = false;
    }

    private System.Windows.Media.SolidColorBrush GetThemeBrush(
        string key)
    {
        if (FindResource(key) is System.Windows.Media.SolidColorBrush brush)
        {
            return brush;
        }

        return new System.Windows.Media.SolidColorBrush(
            _settings.IsDarkTheme
                ? WpfColor.FromRgb(255, 255, 255)
                : WpfColor.FromRgb(25, 25, 30));
    }

    private void ApplyWindowTheme(
        bool dark)
    {
        SetThemeBrush(
            "WindowBackground",
            dark
                ? WpfColor.FromRgb(16, 16, 20)
                : WpfColor.FromRgb(245, 245, 248));

        SetThemeBrush(
            "PanelBackground",
            dark
                ? WpfColor.FromRgb(21, 21, 26)
                : WpfColor.FromRgb(255, 255, 255));

        SetThemeBrush(
            "CardBackground",
            dark
                ? WpfColor.FromRgb(25, 25, 31)
                : WpfColor.FromRgb(255, 255, 255));

        SetThemeBrush(
            "FieldBackground",
            dark
                ? WpfColor.FromRgb(36, 36, 44)
                : WpfColor.FromRgb(239, 239, 244));

        SetThemeBrush(
            "BorderColor",
            dark
                ? WpfColor.FromRgb(41, 41, 49)
                : WpfColor.FromRgb(218, 218, 225));

        SetThemeBrush(
            "PrimaryText",
            dark
                ? WpfColor.FromRgb(242, 242, 245)
                : WpfColor.FromRgb(25, 25, 30));

        SetThemeBrush(
            "SecondaryText",
            dark
                ? WpfColor.FromRgb(133, 133, 143)
                : WpfColor.FromRgb(92, 92, 102));

        SetThemeBrush(
            "MutedText",
            dark
                ? WpfColor.FromRgb(102, 102, 112)
                : WpfColor.FromRgb(115, 115, 125));

        SetThemeBrush(
            "DisabledText",
            dark
                ? WpfColor.FromRgb(119, 119, 128)
                : WpfColor.FromRgb(155, 155, 165));

        SetThemeBrush(
            "HoverBackground",
            dark
                ? WpfColor.FromRgb(36, 36, 44)
                : WpfColor.FromRgb(232, 232, 238));

        SetThemeBrush(
            "PressedBackground",
            dark
                ? WpfColor.FromRgb(28, 28, 34)
                : WpfColor.FromRgb(224, 224, 231));

        SetThemeBrush(
            "AccentColor",
            WpfColor.FromRgb(61, 27, 93));

        SetThemeBrush(
            "AccentHoverColor",
            WpfColor.FromRgb(81, 40, 120));

        SetThemeBrush(
            "AccentPressedColor",
            WpfColor.FromRgb(43, 19, 63));

        SetThemeBrush(
            "IconColor",
            dark
                ? WpfColor.FromRgb(102, 102, 112)
                : WpfColor.FromRgb(74, 74, 84));

        SetThemeBrush(
            "SuccessText",
            dark
                ? WpfColor.FromRgb(80, 190, 120)
                : WpfColor.FromRgb(38, 132, 74));

        Background =
            GetThemeBrush(
                "WindowBackground");

        Foreground =
            GetThemeBrush(
                "PrimaryText");
    }

    private void SetThemeBrush(
        string key,
        WpfColor color)
    {
        Resources[key] =
            new System.Windows.Media.SolidColorBrush(
                color);
    }

}
