using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EDCBViewer.Models;
using EDCBViewer.Services;
using EpgDbReaderService = EDCBViewer.Services.EpgDbReader;

namespace EDCBViewer;

public partial class MainWindow : Window
{
    private AppSettings _settings = AppSettings.Load();
    private EpgDbReaderService    _epgReader       = null!;
    private bool _inEpgSearch     = false;
    private bool _fileFilterActive = false;  // Enter押下時のみtrue、ナビゲート時にリセット

    private List<MediaFile> _mediaFiles = [];
    private List<MediaFile>? _searchFiles;  // Enter検索の再帰列挙結果（null=通常ブラウズ）
    private HashSet<(string Station, DateTime Start)>? _searchEpgKeys;  // EPG照合ヒットの (放送局, 開始時刻[分精度])
    private readonly SyobocalService _syobocal = new();  // しょぼカル連携（最速放送判定）
    private bool _syoboSyncing;
    private HashSet<(string Station, DateTime Start)>? _fastestKeys;  // events.fastest=1 の (サービス名, 開始時刻[分精度])
    private int _rootRetryCount = 3;        // 未接続フォルダの自動再読込の残り回数（無限ループ防止）
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
        ["最速"]       = "IsFastest",
    };

    private string _dirRoot = "";
    private string _currentDirPath = "";
    private bool _dirLoading = false;

    public MainWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        HorizontalWheel.Attach(this);
        _epgReader      = new EpgDbReaderService(_settings.DbConnectionString);
        _dirRoot        = "";
        _currentDirPath = "";
        InitSortHeaders();
        Loaded += (_, _) => StartUp();
    }

    /// <summary>
    /// 起動時。一覧を先に出してから EPG を取り込み、取り込んだ内容で一覧を作り直す。
    /// 取り込みを待ってから表示すると、40 秒ほど何も出ない画面になるため。
    /// </summary>
    private async void StartUp()
    {
        LoadMediaFiles();
        await ImportEpgAsync();
        if (!string.IsNullOrWhiteSpace(_settings.EpgDataFolder)) LoadMediaFiles();
    }

    /// <summary>
    /// 最速放送マークの整備。しょぼカルの生データは MySQL の syobocal_* 専用テーブルに
    /// 保存し（events は一切変更しない）、判定は events との JOIN で都度計算する。
    /// どのマシンから起動しても syobocal_* を共有するため同じ判定が見える。
    /// ① まず DB の現在の syobocal_* + events の JOIN 結果で即マーク反映
    ///    （別マシンが同期済み、または前回起動分ならここで出る）
    /// ② しょぼカルを同期（手持ちファイルの期間。カバー済みなら通信なし）
    /// ③ 同期で更新があれば JOIN 結果を読み直して再反映
    ///
    /// events の蓄積は 2026-06 からなので、それ以前のファイルは events と JOIN できない。
    /// その分はしょぼカルとファイルだけで判定する（GetFastestKeysFromFilesOnly）。
    /// </summary>
    private async void LoadSyobocal()
    {
        if (_syoboSyncing) return;
        _syoboSyncing = true;
        try
        {
            var reader = _epgReader;
            if (!reader.IsConfigured) return;

            // 判定対象のファイル。判定クエリにも渡して events 側を先に絞る
            // （渡さないと局単位で全走査になる）。
            var fileKeys = _mediaFiles.Concat(_searchFiles ?? [])
                .Where(f => !f.IsDirectory && f.ParsedStartTime.HasValue && !string.IsNullOrEmpty(f.ParsedStation))
                .Select(f => (f.ParsedStation, f.ParsedStartTime!.Value))
                .Distinct()
                .ToList();
            if (fileKeys.Count == 0) return;

            var eventsMin = await Task.Run(reader.GetEventsMinStartTime);

            // ① DB の現在値で即マーク
            var (coveredFrom, _, _) = await Task.Run(reader.GetSyobocalMeta);
            if (coveredFrom != 0)
            {
                var keysNow = await Task.Run(() => CollectFastestKeys(reader, eventsMin, coveredFrom, fileKeys));
                if (keysNow != null && !SetEquals(_fastestKeys, keysNow))
                {
                    _fastestKeys = keysNow;
                    RefreshFastestMarks();
                }
            }

            // ② しょぼカル同期（カバー済みなら通信なし）
            var changed = await _syobocal.SyncToDbAsync(
                reader, fileKeys, msg => StatusText.Text = msg);
            if (!changed) return;

            // ③ EPG が無い期間の番組表を、しょぼカルから作った合成行で埋める
            //    （event_id >= SyntheticEventIdBase。実 EPG の行は書き換えない）
            if (eventsMin != null)
            {
                var oldest = fileKeys.Min(k => k.Item2).AddDays(-1);
                if (oldest < eventsMin.Value)
                {
                    StatusText.Text = "過去の番組表を作成中…";
                    var n = await Task.Run(() => reader.BuildSyntheticEvents(oldest, eventsMin.Value));
                    StatusText.Text = n < 0 ? "過去の番組表の作成に失敗: " + reader.LastSyobocalError : "";
                }
            }

            // ④ 読み直して反映
            var (coveredFrom2, _, _) = await Task.Run(reader.GetSyobocalMeta);
            if (coveredFrom2 == 0) return;
            _fastestKeys = await Task.Run(() => CollectFastestKeys(reader, eventsMin, coveredFrom2, fileKeys))
                           ?? _fastestKeys;
            RefreshFastestMarks();
        }
        finally { _syoboSyncing = false; }
    }

    /// <summary>
    /// 最速キーを集める。events がある期間は events と JOIN し（EPG に無い放送を除ける）、
    /// events より前の期間は手持ちファイルとしょぼカルだけで判定する。
    /// </summary>
    private static HashSet<(string, DateTime)>? CollectFastestKeys(
        EpgDbReaderService reader, DateTime? eventsMin, int coveredFromYm,
        List<(string Station, DateTime Time)> fileKeys)
    {
        var result = new HashSet<(string, DateTime)>();
        var any = false;

        var withEvents = eventsMin == null ? [] : fileKeys.Where(k => k.Time >= eventsMin.Value).ToList();
        if (withEvents.Count > 0)
        {
            var keys = reader.GetFastestKeysViaJoin(
                eventsMin!.Value, DateTime.Now, coveredFromYm, withEvents);
            if (keys != null) { result.UnionWith(keys); any = true; }
        }

        var beforeEvents = eventsMin == null ? fileKeys
                         : fileKeys.Where(k => k.Time < eventsMin.Value).ToList();
        if (beforeEvents.Count > 0)
        {
            var hi = eventsMin ?? DateTime.Now;
            var keys = reader.GetFastestKeysFromFilesOnly(
                beforeEvents.Min(k => k.Time).AddDays(-1), hi, coveredFromYm, beforeEvents);
            if (keys != null) { result.UnionWith(keys); any = true; }
        }

        return any ? result : null;
    }

    private static bool SetEquals(HashSet<(string, DateTime)>? a, HashSet<(string, DateTime)> b) =>
        a != null && a.Count == b.Count && a.SetEquals(b);

    private void RefreshFastestMarks()
    {
        ApplyFastestMarks(_mediaFiles);
        if (_searchFiles != null) ApplyFastestMarks(_searchFiles);
        var selected = DirList.SelectedItem as MediaFile;
        ShowDirPage();
        if (selected != null && DirList.Items.Contains(selected))
        {
            DirList.SelectedItem = selected;
            DirList.ScrollIntoView(selected);
        }
    }

    /// <summary>
    /// 最速放送マーク: events.fastest=1 の (サービス名, 開始時刻) セットと
    /// ファイルの (放送局, 開始時刻[分精度]) を照合して IsFastest を立てる。
    /// </summary>
    private void ApplyFastestMarks(List<MediaFile> files)
    {
        var keys = _fastestKeys;
        foreach (var f in files)
            f.IsFastest = keys != null && !f.IsDirectory && f.ParsedStartTime.HasValue &&
                keys.Contains((f.ParsedStation, f.ParsedStartTime.Value));
    }

    private void InitSortHeaders()
    {
        if (DirList.View is GridView dirGv)
            foreach (var col in dirGv.Columns)
                _dirOrigHeaders[col] = col.Header?.ToString() ?? "";
    }

    private sealed record EpgResultItem(EpgEvent Event)
    {
        public string DisplayName         => Event.EventName;
        public string ParsedStation       => Event.ServiceName;
        public string ParsedStartTimeText => Event.StartTime?.ToString("yyyy/MM/dd HH:mm") ?? "";
        public bool   IsDirectory         => false;
    }

    // ─── ディレクトリ ─────────────────────────────────────────────────────────

    private void NavigateDir(string path)
    {
        _currentDirPath  = path;
        _inEpgSearch     = false;
        _fileFilterActive = false;
        _searchFiles     = null;
        _searchEpgKeys   = null;
        _dirCurrentPage  = 0;
        _rootRetryCount  = 3;
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
            var roots = _settings.EncodedFolders.Where(f => !string.IsNullOrEmpty(f)).ToList();
            if (roots.Count == 0)
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

            var merged = await Task.Run(() =>
            {
                // フォルダ単位で例外を隔離。fn() の呼び出しから列挙・Select 変換まで
                // すべてを try 内で完結させ、遅延評価による例外漏れを防ぐ。
                static List<T> TryList<T>(Func<IEnumerable<T>> fn)
                {
                    try { return fn().ToList(); } catch { return []; }
                }

                var dirs = roots
                    .SelectMany(r => TryList(() =>
                        new DirectoryInfo(r).EnumerateDirectories()
                            .Select(di => new MediaFile
                            {
                                FilePath     = di.FullName,
                                IsDirectory  = true,
                                LastModified = di.LastWriteTime,
                            })))
                    .OrderBy(d => d.DisplayName)
                    .ToList();

                var files = roots
                    .SelectMany(r => new[] { "*.ts", "*.m2ts", "*.mp4" }.SelectMany(pat =>
                        TryList(() =>
                            new DirectoryInfo(r).EnumerateFiles(pat)
                                .Select(fi => new MediaFile
                                {
                                    FilePath     = fi.FullName,
                                    FileSize     = fi.Length,
                                    LastModified = fi.LastWriteTime,
                                }))))
                    .OrderByDescending(f => f.ParsedStartTime ?? f.LastModified)
                    .ToList();

                return dirs.Concat(files).ToList();
            });

            _mediaFiles     = merged;
            ApplyFastestMarks(_mediaFiles);
            _inEpgSearch    = false;
            _dirCurrentPage = 0;
            UpdateDirAddressBar();
            ShowDirPage();
            DirList.Focus();
            LoadSyobocal();

            // 起動直後にネットワーク未接続だったフォルダがあれば 5 秒後に再読込。
            // 空フォルダも「未接続」と区別できないため、リトライは回数制限付き
            // （無制限だと空の起点フォルダ1つで5秒ごとの再読込が永久に続く）
            var failedRoots = roots.Where(r =>
                !merged.Any(f => f.FilePath.StartsWith(
                    r.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))).ToList();
            if (failedRoots.Count > 0 && _rootRetryCount > 0)
            {
                _rootRetryCount--;
                _ = Task.Delay(5000).ContinueWith(_ =>
                    Dispatcher.InvokeAsync(() =>
                    {
                        if (!_dirLoading && string.IsNullOrEmpty(_currentDirPath) && !_fileFilterActive)
                            LoadMediaFiles();
                    }));
            }

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
        ApplyFastestMarks(_mediaFiles);
        _dirCurrentPage = 0;
        UpdateDirAddressBar();
        ShowDirPage();
        DirList.Focus();
        LoadSyobocal();
    }

    private void UpdateDirAddressBar()
    {
        var atRoot = string.IsNullOrEmpty(_currentDirPath);
        DirAddressBar.Visibility = atRoot ? Visibility.Collapsed : Visibility.Visible;
        DirPathBox.Text = _currentDirPath;
        DirPathBox.CaretIndex = DirPathBox.Text.Length;
    }

    private void DirPathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var path = DirPathBox.Text.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(path))
        {
            NavigateDir("");
            return;
        }
        if (!Directory.Exists(path))
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
        // 検索モード・最速のみ表示中はファイルのみを対象にし、フォルダは表示しない
        var fastestOnly = FastestOnlyCheck.IsChecked == true;
        IEnumerable<MediaFile> dirs = (_fileFilterActive && _searchFiles != null) || fastestOnly
            ? []
            : _mediaFiles.Where(f => f.IsDirectory).OrderBy(d => d.DisplayName);
        IEnumerable<MediaFile> files = _fileFilterActive && _searchFiles != null
            ? _searchFiles
            : _mediaFiles.Where(f => !f.IsDirectory);
        if (fastestOnly)
            files = files.Where(f => f.IsFastest);

        if (_fileFilterActive)
        {
            // ファイル名側は NFKC 正規化して全角半角を無視。
            // EDCB の Title2 マクロは [4K][HDR][字] 等のタグをファイル名から除去するため、
            // ファイル名不一致でも EPG 照合（放送局+開始時刻）でヒットすれば結果に含める
            var terms = DirSearchBox.Text.Trim()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(MediaFile.NormalizeForSearch)
                .ToArray();
            if (terms.Length > 0)
                files = files.Where(f =>
                    terms.All(t => f.SearchText.Contains(t, StringComparison.OrdinalIgnoreCase)) ||
                    (_searchEpgKeys != null && f.ParsedStartTime.HasValue &&
                     _searchEpgKeys.Contains((f.ParsedStation, f.ParsedStartTime.Value))));
        }

        if (_dirSortProp != null)
        {
            var asc = _dirSortAsc;
            Comparison<MediaFile> cmp = _dirSortProp switch
            {
                "ParsedTitle"     => (a, b) => string.Compare(a.ParsedTitle,   b.ParsedTitle,   StringComparison.CurrentCulture),
                "ParsedStation"   => (a, b) => string.Compare(a.ParsedStation, b.ParsedStation, StringComparison.CurrentCulture),
                "ParsedStartTime" => (a, b) => (a.ParsedStartTime ?? DateTime.MinValue).CompareTo(b.ParsedStartTime ?? DateTime.MinValue),
                // 最速列は初回クリック（▲）で最速が先頭に来るよう降順比較。同順位は放送日時の新しい順
                "IsFastest"       => (a, b) =>
                {
                    var c = b.IsFastest.CompareTo(a.IsFastest);
                    return c != 0 ? c : (b.ParsedStartTime ?? b.LastModified).CompareTo(a.ParsedStartTime ?? a.LastModified);
                },
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

        var selected = DirList.SelectedItem as MediaFile;

        ApplySort(col, prop, ref _dirActiveSortCol, ref _dirSortProp, ref _dirSortAsc, _dirOrigHeaders);

        // ソート後も選択中の項目を保持し、その項目が含まれるページを表示する
        _dirCurrentPage = 0;
        if (selected != null)
        {
            var idx = GetFilteredFiles().IndexOf(selected);
            if (idx >= 0) _dirCurrentPage = idx / DirPageSize;
        }
        ShowDirPage();
        if (selected != null && DirList.Items.Contains(selected))
        {
            DirList.SelectedItem = selected;
            DirList.ScrollIntoView(selected);
        }
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
        if (_inEpgSearch) return;
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
        if (!string.IsNullOrEmpty(DirSearchBox.Text)) return;
        _inEpgSearch      = false;
        _fileFilterActive = false;
        _searchFiles      = null;
        _searchEpgKeys    = null;
        _dirCurrentPage   = 0;
        ShowDirPage();
    }

    private async void DirSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var q = DirSearchBox.Text.Trim();

        if (string.IsNullOrEmpty(q))
        {
            _inEpgSearch      = false;
            _fileFilterActive = false;
            _searchFiles      = null;
            _searchEpgKeys    = null;
            _dirCurrentPage   = 0;
            ShowDirPage();
            DirList.Focus();
            return;
        }

        // 検索結果に表示するのはファイルのみ（EPG イベントは表示しない）。
        // 現在のスコープ（ルート表示なら全起点フォルダ、フォルダ内ならそのフォルダ）
        // 以下を再帰列挙し、スペース区切り AND でフィルタする。
        // 加えて EPG DB の番組名・説明文にもキーワードを照合し、ヒットした番組の
        // (放送局, 開始時刻) に対応するファイルも結果に含める
        // （[HDR] 等のタグはファイル名から除去されており EPG にしか存在しないため）
        StatusText.Text = "検索中...";
        List<string> scopes = string.IsNullOrEmpty(_currentDirPath)
            ? _settings.EncodedFolders.Where(f => !string.IsNullOrEmpty(f)).ToList()
            : [_currentDirPath];
        var terms  = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var reader = _epgReader;
        (_searchFiles, _searchEpgKeys) = await Task.Run(() =>
        {
            var files = EnumerateFilesRecursive(scopes);
            var keys  = reader.GetMatchingEventKeys(terms)
                .Select(k => (k.ServiceName, TruncateToMinute(k.StartTime)))
                .ToHashSet();
            return (files, keys);
        });
        ApplyFastestMarks(_searchFiles);

        _inEpgSearch      = false;
        _fileFilterActive = true;
        _dirCurrentPage   = 0;
        ShowDirPage();

        if (DirList.Items.Count > 0) DirList.SelectedIndex = 0;
        DirList.Focus();
        LoadSyobocal();
    }

    // ファイルの ParsedStartTime は分精度なので、EPG 側の開始時刻も分に切り詰めて照合する
    private static DateTime TruncateToMinute(DateTime t) =>
        t.AddTicks(-(t.Ticks % TimeSpan.TicksPerMinute));

    private static List<MediaFile> EnumerateFilesRecursive(List<string> roots)
    {
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        var result = new List<MediaFile>();
        foreach (var root in roots)
        {
            foreach (var pat in new[] { "*.ts", "*.m2ts", "*.mp4" })
            {
                try
                {
                    result.AddRange(new DirectoryInfo(root).EnumerateFiles(pat, opts)
                        .Select(fi => new MediaFile
                        {
                            FilePath     = fi.FullName,
                            FileSize     = fi.Length,
                            LastModified = fi.LastWriteTime,
                        })
                        .ToList());
                }
                catch { }
            }
        }
        return result.OrderByDescending(f => f.ParsedStartTime ?? f.LastModified).ToList();
    }

    private void DirList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DirList.View is not GridView gv) return;
        var fixed_ = gv.Columns.Skip(1).Sum(c => c.ActualWidth);
        gv.Columns[0].Width = Math.Max(100, DirList.ActualWidth - fixed_ - 22);
    }

    private void DirRefresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void FastestOnly_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _dirCurrentPage = 0;
        ShowDirPage();
    }

    private async void DirList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_inEpgSearch)
        {
            _selectedMediaFile = null;
            if (DirList.SelectedItem is not EpgResultItem epgItem) { ClearDirPanel(); return; }
            DirTitle.Text    = epgItem.Event.EventName;
            DirService.Text  = epgItem.Event.ServiceName;
            DirDateTime.Text = epgItem.ParsedStartTimeText;
            DirFilePath.Text = "";
            DirDrops.Text    = "";
            var infoText = string.IsNullOrEmpty(epgItem.Event.ExtText)   ? epgItem.Event.ShortText
                         : string.IsNullOrEmpty(epgItem.Event.ShortText) ? epgItem.Event.ExtText
                         : epgItem.Event.ShortText + "\n" + epgItem.Event.ExtText;
            var hasInfo = !string.IsNullOrEmpty(infoText);
            DirProgramInfoLabel.Visibility = hasInfo ? Visibility.Visible : Visibility.Collapsed;
            DirProgramInfo.Visibility      = DirProgramInfoLabel.Visibility;
            DirProgramInfo.Text            = infoText;
            DirPlayButton.Visibility       = Visibility.Collapsed;
            return;
        }

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

        // ファイル名から即時表示
        DirTitle.Text = file.ParsedTitle;
        DirService.Text = file.ParsedStation;
        DirDateTime.Text = file.ParsedStartTimeText;
        DirDrops.Text = "";
        DirProgramInfoLabel.Visibility = Visibility.Collapsed;
        DirProgramInfo.Visibility = Visibility.Collapsed;

        // ドロップ／スクランブル（録画ファイル隣の .err）と番組情報（events）は
        // 互いに独立した I/O なので、直列に待たず並行して取りに行く
        var dropsPath = file.FilePath;
        var dropsTask = Task.Run(() => TsErrInfo.Format(dropsPath));

        // events テーブルをサービス名＋開始時刻で検索して番組情報を取得。
        // ヒットしたらタイトルも EPG 側の正式タイトル（Title2 マクロでファイル名からは
        // 除去される [4K][HDR][字] 等のタグ付き）に差し替える
        Task<EpgDbReaderService.EventDisplayInfo?>? progTask = null;
        if (file.ParsedStartTime.HasValue && !string.IsNullOrEmpty(file.ParsedStation))
        {
            var station   = file.ParsedStation;
            var startTime = file.ParsedStartTime.Value;
            progTask = Task.Run(() =>
                new EpgDbReaderService(_settings.DbConnectionString)
                    .GetEventInfoByStationAndTime(station, startTime, file.ParsedTitle));
        }

        var dropsText = await dropsTask;
        var prog = progTask == null ? null : await progTask;

        // 待っている間に選択が変わっていたら、古い結果で上書きしない
        if (!ReferenceEquals(DirList.SelectedItem, file)) return;

        DirDrops.Text = dropsText;

        if (progTask != null)
        {
            if (!string.IsNullOrEmpty(prog?.EventName))
                DirTitle.Text = prog.EventName;

            var hasInfo = !string.IsNullOrEmpty(prog?.InfoText);
            DirProgramInfoLabel.Visibility = hasInfo ? Visibility.Visible : Visibility.Collapsed;
            DirProgramInfo.Visibility = DirProgramInfoLabel.Visibility;
            DirProgramInfo.Text = prog?.InfoText ?? "";
        }
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
            InitialDirectory = string.IsNullOrEmpty(_currentDirPath)
                ? (_settings.EncodedFolders.FirstOrDefault() ?? "")
                : _currentDirPath,
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
                _rootRetryCount = 3;
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

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private bool _refreshing;

    /// <summary>
    /// 更新（F5 / 更新ボタン）。
    /// EPG を DB に取り込んでからファイル一覧を読み直す。
    /// 取り込みに失敗しても続行する（今 DB にあるデータで一覧は出せる）。
    /// </summary>
    private async void Refresh()
    {
        _rootRetryCount = 3;
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            await ImportEpgAsync();
            LoadMediaFiles();
        }
        finally
        {
            RefreshMenuItem.IsEnabled = true;
            _refreshing = false;
        }
    }

    /// <summary>
    /// *_epg.dat を読んで DB に取り込む。フォルダ未設定なら何もしない。
    /// DLL の読み込みも DB 書き込みも重いので、必ずワーカースレッドで動かす。
    /// </summary>
    private async Task ImportEpgAsync()
    {
        var dir = _settings.EpgDataFolder;
        if (string.IsNullOrWhiteSpace(dir)) return;

        RefreshMenuItem.IsEnabled = false;
        StatusText.Text = "EPG取り込み中…";
        var conn = _settings.DbConnectionString;
        var r = await Task.Run(() => EpgImporter.Run(
            conn, dir, msg => Dispatcher.Invoke(() => StatusText.Text = msg)));
        StatusText.Text = r.Message;
        RefreshMenuItem.IsEnabled = true;
    }

    private EpgGuideWindow? _guideWindow;

    private void EpgGuide_Click(object sender, RoutedEventArgs e)
    {
        if (_guideWindow is { IsLoaded: true })
        {
            _guideWindow.Activate();
            return;
        }
        _guideWindow = new EpgGuideWindow(_settings.DbConnectionString);
        _guideWindow.Show();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow(_settings);
        if (win.ShowDialog() == true)
        {
            _settings = win.Settings;
            _settings.Save();
            _epgReader      = new EpgDbReaderService(_settings.DbConnectionString);
            _dirRoot        = "";
            _currentDirPath = "";
            _rootRetryCount = 3;
            LoadMediaFiles();
        }
    }
}
