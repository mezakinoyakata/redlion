using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EDCBViewer.Models;
using EDCBViewer.Services;
using RecordingIndexService = EDCBViewer.Services.RecordingIndex;
using EpgDbReaderService = EDCBViewer.Services.EpgDbReader;

namespace EDCBViewer;

public partial class MainWindow : Window
{
    private AppSettings _settings = AppSettings.Load();
    private RecordingIndexService _recordingIndex = null!;

    private List<MediaFile> _mediaFiles = [];
    private MediaFile? _selectedMediaFile;
    private int _dirCurrentPage = 0;
    private int DirPageSize => _settings.MaxRecItems;
    private int DirTotalPages(int filteredCount) => Math.Max(1, (filteredCount + DirPageSize - 1) / DirPageSize);

    private string? _dirSortProp;
    private bool    _dirSortAsc = true;
    private GridViewColumn? _dirActiveSortCol;
    private readonly Dictionary<GridViewColumn, string> _dirOrigHeaders = new();

    private static readonly Dictionary<string, string> DirSortProps = new()
    {
        ["ファイル名"] = "ParsedTitle",
        ["放送局"]     = "ParsedStation",
        ["放送日時"]   = "ParsedStartTime",
    };

    private string _dirRoot = "";
    private string _currentDirPath = "";
    private bool _dirLoading = false;

    public MainWindow()
    {
        InitializeComponent();
        _recordingIndex = new RecordingIndexService(_settings.DbConnectionString);
        var root = _settings.EncodedFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _dirRoot = root;
        _currentDirPath = root;
        InitSortHeaders();
        _recordingIndex.Load();
        Loaded += (_, _) => LoadMediaFiles();
    }

    private void InitSortHeaders()
    {
        if (DirList.View is GridView dirGv)
            foreach (var col in dirGv.Columns)
                _dirOrigHeaders[col] = col.Header?.ToString() ?? "";
    }

    // ─── ディレクトリ ─────────────────────────────────────────────────────────

    private void NavigateDir(string path)
    {
        _currentDirPath = path;
        DirSearchBox.Text = "";
        _dirCurrentPage = 0;
        LoadMediaFiles();
    }

    private async void LoadMediaFiles()
    {
        if (_dirLoading) return;
        _dirLoading = true;
        try { await LoadMediaFilesCore(); }
        finally { _dirLoading = false; }
    }

    private async Task LoadMediaFilesCore()
    {
        var folder = _currentDirPath;
        if (string.IsNullOrWhiteSpace(folder))
        {
            _mediaFiles = [];
            DirList.ItemsSource = null;
            StatusText.Text = "フォルダが未設定です。設定 → パス設定... で指定してください。";
            DirPathBox.Text = "";
            return;
        }

        _mediaFiles = [];
        DirList.ItemsSource = null;
        StatusText.Text = "ファイル一覧を読み込み中...";

        List<MediaFile> all;
        try
        {
            all = await Task.Run(() =>
            {
                if (!Directory.Exists(folder)) return [];

                var dirs = new DirectoryInfo(folder)
                    .EnumerateDirectories()
                    .Select(di => new MediaFile
                    {
                        FilePath = di.FullName,
                        IsDirectory = true,
                        LastModified = di.LastWriteTime,
                    })
                    .OrderBy(d => d.DisplayName)
                    .ToList();

                var files = new[] { "*.ts", "*.m2ts", "*.mp4" }
                    .SelectMany(pat => new DirectoryInfo(folder).EnumerateFiles(pat))
                    .Select(fi => new MediaFile
                    {
                        FilePath = fi.FullName,
                        FileSize = fi.Length,
                        LastModified = fi.LastWriteTime,
                    })
                    .OrderByDescending(f => f.ParsedStartTime ?? f.LastModified)
                    .ToList();

                return dirs.Concat(files).ToList();
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"読み込みエラー: {ex.Message}";
            return;
        }

        _mediaFiles = all;
        _dirCurrentPage = 0;
        UpdateDirAddressBar();
        ShowDirPage();
        DirList.Focus();
    }

    private void UpdateDirAddressBar()
    {
        DirPathBox.Text = _currentDirPath;
        DirPathBox.CaretIndex = DirPathBox.Text.Length;
    }

    private void DirPathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var path = DirPathBox.Text.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            DirPathBox.Text = _currentDirPath;
            StatusText.Text = $"フォルダが見つかりません: {DirPathBox.Text.Trim()}";
            return;
        }
        if (!path.StartsWith(_dirRoot, StringComparison.OrdinalIgnoreCase))
            _dirRoot = path;
        NavigateDir(path);
    }

    private List<MediaFile> GetFilteredFiles()
    {
        var dirs = _mediaFiles.Where(f => f.IsDirectory).OrderBy(d => d.DisplayName);
        IEnumerable<MediaFile> files = _mediaFiles.Where(f => !f.IsDirectory);

        var q = DirSearchBox.Text.Trim();
        if (!string.IsNullOrEmpty(q))
            files = files.Where(f =>
                f.ParsedTitle.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                f.ParsedStation.Contains(q, StringComparison.OrdinalIgnoreCase));

        if (_dirSortProp != null)
        {
            var asc = _dirSortAsc;
            Comparison<MediaFile> cmp = _dirSortProp switch
            {
                "ParsedTitle"     => (a, b) => string.Compare(a.ParsedTitle,   b.ParsedTitle,   StringComparison.CurrentCulture),
                "ParsedStation"   => (a, b) => string.Compare(a.ParsedStation, b.ParsedStation, StringComparison.CurrentCulture),
                "ParsedStartTime" => (a, b) => (a.ParsedStartTime ?? DateTime.MinValue).CompareTo(b.ParsedStartTime ?? DateTime.MinValue),
                _                 => (a, b) => (a.ParsedStartTime ?? a.LastModified).CompareTo(b.ParsedStartTime ?? b.LastModified),
            };
            var fileList = files.ToList();
            fileList.Sort(asc ? cmp : (a, b) => -cmp(a, b));
            return dirs.Concat(fileList).ToList();
        }

        return dirs.Concat(files).ToList();
    }

    private void DirList_ColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header || header.Column == null) return;
        var col = header.Column;
        var origText = _dirOrigHeaders.TryGetValue(col, out var t) ? t : col.Header?.ToString() ?? "";
        if (!DirSortProps.TryGetValue(origText, out var prop)) return;

        ApplySort(col, prop, ref _dirActiveSortCol, ref _dirSortProp, ref _dirSortAsc, _dirOrigHeaders);
        _dirCurrentPage = 0;
        ShowDirPage();
    }

    private static void ApplySort(
        GridViewColumn col,
        string prop,
        ref GridViewColumn? activeCol,
        ref string? sortProp,
        ref bool sortAsc,
        Dictionary<GridViewColumn, string> origHeaders)
    {
        if (sortProp == prop)
        {
            sortAsc = !sortAsc;
        }
        else
        {
            if (activeCol != null && origHeaders.TryGetValue(activeCol, out var prev))
                activeCol.Header = prev;
            sortProp = prop;
            sortAsc = true;
        }
        activeCol = col;
        var origText = origHeaders.TryGetValue(col, out var t) ? t : prop;
        col.Header = $"{origText} {(sortAsc ? "▲" : "▼")}";
    }

    private void ShowDirPage()
    {
        var filtered = GetFilteredFiles();
        var total = filtered.Count;
        var totalPages = DirTotalPages(total);
        _dirCurrentPage = Math.Clamp(_dirCurrentPage, 0, totalPages - 1);

        DirList.ItemsSource = filtered
            .Skip(_dirCurrentPage * DirPageSize)
            .Take(DirPageSize)
            .ToList();

        var hasPrev = _dirCurrentPage > 0;
        var hasNext = _dirCurrentPage < totalPages - 1;
        DirFirstPageButton.IsEnabled = hasPrev;
        DirPrevPageButton.IsEnabled  = hasPrev;
        DirNextPageButton.IsEnabled  = hasNext;
        DirLastPageButton.IsEnabled  = hasNext;
        DirPageLabel.Text  = totalPages > 1 ? $"{_dirCurrentPage + 1} / {totalPages} ページ" : "";
        DirCountText.Text  = $"{total:#,0} 件";

        StatusText.Text = total > 0
            ? $"{total:#,0} 件"
            : "一致するファイルがありません";
    }

    private void DirFirstPage_Click(object sender, RoutedEventArgs e) { _dirCurrentPage = 0; ShowDirPage(); }
    private void DirPrevPage_Click(object sender, RoutedEventArgs e)  { _dirCurrentPage--; ShowDirPage(); }
    private void DirNextPage_Click(object sender, RoutedEventArgs e)  { _dirCurrentPage++; ShowDirPage(); }
    private void DirLastPage_Click(object sender, RoutedEventArgs e)  { _dirCurrentPage = int.MaxValue; ShowDirPage(); }

    private void DirSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(DirSearchBox.Text))
        {
            _dirCurrentPage = 0;
            ShowDirPage();
        }
    }

    private void DirSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _dirCurrentPage = 0;
        ShowDirPage();
        if (DirList.Items.Count > 0) DirList.SelectedIndex = 0;
        DirList.Focus();
    }

    private void DirList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DirList.View is not GridView gv) return;
        var fixed_ = gv.Columns.Skip(1).Sum(c => c.ActualWidth);
        gv.Columns[0].Width = Math.Max(100, DirList.ActualWidth - fixed_ - 22);
    }

    private void DirRefresh_Click(object sender, RoutedEventArgs e) => LoadMediaFiles();

    private async void DirList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DirList.SelectedItem is not MediaFile file)
        {
            ClearDirPanel();
            return;
        }

        _selectedMediaFile = file;
        DirFilePath.Text = file.FilePath;

        if (file.IsDirectory)
        {
            DirTitle.Text = Path.GetFileName(file.FilePath);
            DirService.Text = "";
            DirDateTime.Text = "";
            DirDrops.Text = "";
            DirProgramInfoLabel.Visibility = Visibility.Collapsed;
            DirProgramInfo.Visibility = Visibility.Collapsed;
            DirPlayButton.Visibility = Visibility.Collapsed;
            return;
        }

        DirPlayButton.Visibility = Visibility.Visible;

        // ファイル名から即時表示（ブロッキングなし）
        DirTitle.Text = file.ParsedTitle;
        DirService.Text = file.ParsedStation;
        DirDateTime.Text = file.ParsedStartTimeText;
        DirDrops.Text = "";
        DirProgramInfoLabel.Visibility = Visibility.Collapsed;
        DirProgramInfo.Visibility = Visibility.Collapsed;

        // MySQL で非同期補完（接続不可でも UI をブロックしない）
        RecordingIndexEntry? entry;
        try { entry = await Task.Run(() => _recordingIndex.Find(file.FileName)); }
        catch { entry = null; }

        if (!ReferenceEquals(DirList.SelectedItem, file) || entry == null) return;

        DirTitle.Text = entry.FullTitle;
        DirService.Text = entry.ServiceName;
        DirDateTime.Text = $"{entry.StartTime:yyyy/MM/dd HH:mm} ({entry.DurationText})";
        var errSuffix = string.IsNullOrWhiteSpace(entry.Comment) ? "" : $"  [{entry.Comment.Trim()}]";
        DirDrops.Text = $"ドロップ: {entry.Drops}  スクランブル: {entry.Scrambles}{errSuffix}";

        var progInfo = entry.EventID != 0
            ? await Task.Run(() => new EpgDbReaderService(_settings.DbConnectionString).GetEventInfoText(
                entry.OriginalNetworkID, entry.TransportStreamID, entry.ServiceID, entry.EventID))
              ?? entry.ProgramInfo
            : entry.ProgramInfo;

        if (!ReferenceEquals(DirList.SelectedItem, file)) return;

        var hasInfo = !string.IsNullOrEmpty(progInfo);
        DirProgramInfoLabel.Visibility = hasInfo ? Visibility.Visible : Visibility.Collapsed;
        DirProgramInfo.Visibility = DirProgramInfoLabel.Visibility;
        DirProgramInfo.Text = progInfo;
    }

    private void DirList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DirList.SelectedItem is MediaFile file)
        {
            if (file.IsDirectory) NavigateDir(file.FilePath);
            else OpenWithPlayer(file.FilePath);
        }
    }

    private void DirBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "フォルダを選択",
            InitialDirectory = string.IsNullOrEmpty(_currentDirPath) ? _dirRoot : _currentDirPath,
        };
        if (dialog.ShowDialog() != true) return;
        var selected = dialog.FolderName;
        if (!selected.StartsWith(_dirRoot, StringComparison.OrdinalIgnoreCase))
            _dirRoot = selected.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        NavigateDir(selected);
    }

    private void DirPlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMediaFile != null)
            OpenWithPlayer(_selectedMediaFile.FilePath);
    }

    private void ClearDirPanel()
    {
        _selectedMediaFile = null;
        DirTitle.Text = "";
        DirService.Text = "";
        DirDateTime.Text = "";
        DirFilePath.Text = "";
        DirDrops.Text = "";
        DirProgramInfo.Text = "";
        DirProgramInfoLabel.Visibility = Visibility.Collapsed;
        DirProgramInfo.Visibility = Visibility.Collapsed;
        DirPlayButton.Visibility = Visibility.Collapsed;
    }

    // ─── 再生 ─────────────────────────────────────────────────────────────────

    private void OpenWithPlayer(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        if (!File.Exists(_settings.PlayerPath))
        {
            MessageBox.Show($"プレイヤーが見つかりません:\n{_settings.PlayerPath}\n\n設定でパスを変更してください。",
                "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!File.Exists(path))
        {
            StatusText.Text = $"ファイルが見つかりません: {path}";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _settings.PlayerPath,
                Arguments = $"\"{path}\"",
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"再生できませんでした:\n{ex.Message}", "エラー",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ─── キーボード ───────────────────────────────────────────────────────────

    private void Window_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox tb && !tb.IsReadOnly) return;
        DirSearchBox.Focus();
        DirSearchBox.AppendText(e.Text);
        DirSearchBox.CaretIndex = DirSearchBox.Text.Length;
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox tb && !tb.IsReadOnly) return;
        switch (e.Key)
        {
            case Key.PageDown: DirNextPage_Click(this, new RoutedEventArgs()); e.Handled = true; break;
            case Key.PageUp:   DirPrevPage_Click(this, new RoutedEventArgs()); e.Handled = true; break;
            case Key.Home:     DirFirstPage_Click(this, new RoutedEventArgs()); e.Handled = true; break;
            case Key.End:      DirLastPage_Click(this, new RoutedEventArgs()); e.Handled = true; break;
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        var isEditable = Keyboard.FocusedElement is TextBox tb && !tb.IsReadOnly;
        switch (e.Key)
        {
            case Key.F5:
                LoadMediaFiles();
                e.Handled = true;
                break;
            case Key.Enter when !isEditable:
                if (_selectedMediaFile != null)
                {
                    if (_selectedMediaFile.IsDirectory) NavigateDir(_selectedMediaFile.FilePath);
                    else OpenWithPlayer(_selectedMediaFile.FilePath);
                }
                e.Handled = true;
                break;
            case Key.Up when !isEditable:
                MoveDirListSelection(-1);
                e.Handled = true;
                break;
            case Key.Down when !isEditable:
                MoveDirListSelection(+1);
                e.Handled = true;
                break;
        }
    }

    private void MoveDirListSelection(int delta)
    {
        var count = DirList.Items.Count;
        if (count == 0) return;
        var next = Math.Clamp(DirList.SelectedIndex + delta, 0, count - 1);
        DirList.SelectedIndex = next;
        DirList.ScrollIntoView(DirList.SelectedItem);
    }

    // ─── メニュー / 設定 ─────────────────────────────────────────────────────

    private void Refresh_Click(object sender, RoutedEventArgs e) => LoadMediaFiles();

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow(_settings);
        if (win.ShowDialog() == true)
        {
            _settings = win.Settings;
            _settings.Save();
            _recordingIndex = new RecordingIndexService(_settings.DbConnectionString);
            _recordingIndex.Load();
            var root = _settings.EncodedFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            _dirRoot = root;
            _currentDirPath = root;
            LoadMediaFiles();
        }
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        _recordingIndex.Dispose();
    }
}
