using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using EDCBViewer.Models;
using EDCBViewer.Parsers;
using EDCBViewer.Services;
using RecordingIndexService = EDCBViewer.Services.RecordingIndex;

namespace EDCBViewer;

public partial class MainWindow : Window
{
    private AppSettings _settings = AppSettings.Load();
    private RecFileInfo? _selectedRec;
    private List<RecFileInfo> _recList = [];    // 現在ページ分
    private List<RecFileInfo> _allRecList = []; // 検索用全件（バックグラウンド取得）
    private List<ReserveData> _resList = [];

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

    // フィルタ + ソート用 CollectionView
    private ICollectionView? _recView;    // ページビュー

    // ソート状態
    private string? _recSortProp;
    private bool _recSortAsc = true;
    private GridViewColumn? _recActiveSortCol;

    // 全件ページモード（ソート or 検索 → _allRecList ベースのページング）
    private bool _isAllRecPagedMode = false;  // 全件ページモード中
    private int  _allRecPagedPage   = 0;      // 全件ページモード内の現在ページ
    private List<RecFileInfo> _pagedAllRec = []; // フィルタ+ソート済みスナップショット
    private int AllRecPagedTotalPages => _pagedAllRec.Count <= 0 ? 1
        : (_pagedAllRec.Count + PageSize - 1) / PageSize;

    private string? _resSortProp;
    private bool _resSortAsc = true;
    private GridViewColumn? _resActiveSortCol;

    // 直前に表示したエラーメッセージ（同じエラーを連続ポップアップしない）
    private string? _lastErrMsg;

    // 列ヘッダーの元テキスト（ソートインジケータ付与前の値を保持）
    private readonly Dictionary<GridViewColumn, string> _recOrigHeaders = new();
    private readonly Dictionary<GridViewColumn, string> _resOrigHeaders = new();

    // 列ヘッダーテキスト → ソートプロパティ名
    private static readonly Dictionary<string, string> RecSortProps = new()
    {
        ["タイトル"]  = "Title",
        ["放送局"]    = "ServiceName",
        ["開始日時"]  = "StartTime",
        ["時間"]      = "DurationSecond",
        ["状態"]      = "RecStatus",
    };

    private static readonly Dictionary<string, string> ResSortProps = new()
    {
        ["タイトル"]  = "Title",
        ["放送局"]    = "StationName",
        ["開始日時"]  = "StartTime",
        ["時間"]      = "DurationSecond",
        ["状態"]      = "ReserveStatus",
    };

    public MainWindow()
    {
        InitializeComponent();
        _recordingIndex = new RecordingIndexService(_settings.DbConnectionString);
        InitSortHeaders();
        _recordingIndex.Load();
        Loaded += (_, _) => Reload();
    }

    /// <summary>列ヘッダーの元テキストを記録する（ソートインジケータのリセット用）</summary>
    private void InitSortHeaders()
    {
        if (RecInfoList.View is GridView recGv)
            foreach (var col in recGv.Columns)
                _recOrigHeaders[col] = col.Header?.ToString() ?? "";

        if (ReserveList.View is GridView resGv)
            foreach (var col in resGv.Columns)
                _resOrigHeaders[col] = col.Header?.ToString() ?? "";

        if (DirList.View is GridView dirGv)
            foreach (var col in dirGv.Columns)
                _dirOrigHeaders[col] = col.Header?.ToString() ?? "";
    }

    private bool _isLoading;
    private int  _recTotal;                           // サーバー上の全録画件数
    private int  _currentPage;                        // 現在のページ（0始まり）
    private CancellationTokenSource _allRecCts = new();

    // 全文検索用キャッシュ: ID → programInfo テキスト
    // _recList と _allRecList は別オブジェクトのため共有辞書で橋渡し。
    // バックグラウンドスレッドと UI スレッドが同時アクセスするため ConcurrentDictionary を使用。
    // 起動時にディスクから復元し、終了時・取得完了時に保存する。
    private readonly ConcurrentDictionary<uint, string> _programInfoCache = LoadProgInfoCacheFromDisk();

    private static ConcurrentDictionary<uint, string> LoadProgInfoCacheFromDisk()
    {
        try
        {
            var path = AppSettings.ProgInfoCachePath;
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (dict != null)
                {
                    var result = new ConcurrentDictionary<uint, string>();
                    foreach (var kv in dict)
                        if (uint.TryParse(kv.Key, out var id))
                            result[id] = kv.Value ?? "";
                    return result;
                }
            }
        }
        catch { }
        return new ConcurrentDictionary<uint, string>();
    }

    private void SaveProgInfoCache()
    {
        try
        {
            var path = AppSettings.ProgInfoCachePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var dict = new Dictionary<string, string>(_programInfoCache.Count);
            foreach (var kv in _programInfoCache)
                dict[kv.Key.ToString()] = kv.Value;
            var json = JsonSerializer.Serialize(dict);
            File.WriteAllText(path, json);
        }
        catch { }
    }

    private int PageSize => _settings.MaxRecItems;
    private int TotalPages => _recTotal <= 0 ? 1 : (_recTotal + PageSize - 1) / PageSize;

    private void FirstPage_Click(object sender, RoutedEventArgs e) => LoadPage(0);
    private void PrevPage_Click(object sender, RoutedEventArgs e)  => LoadPage((_isAllRecPagedMode ? _allRecPagedPage : _currentPage) - 1);
    private void NextPage_Click(object sender, RoutedEventArgs e)  => LoadPage((_isAllRecPagedMode ? _allRecPagedPage : _currentPage) + 1);
    private void LastPage_Click(object sender, RoutedEventArgs e)  => LoadPage(_isAllRecPagedMode ? AllRecPagedTotalPages - 1 : TotalPages - 1);

    private void LoadPage(int page)
    {
        if (_isAllRecPagedMode)
        {
            // 全件ページモード: _pagedAllRec 内をページ移動（サーバー通信なし）
            _allRecPagedPage = Math.Clamp(page, 0, Math.Max(0, AllRecPagedTotalPages - 1));
            ShowAllRecPage();
            return;
        }
        _currentPage = Math.Clamp(page, 0, Math.Max(0, TotalPages - 1));
        Reload(resetPage: false);
    }

    private async void Reload(bool resetPage = true)
    {
        if (_isLoading) return;
        try { await ReloadCore(resetPage); }
        finally { _isLoading = false; RefreshMenuItem.IsEnabled = true; }
    }

    private async Task ReloadCore(bool resetPage)
    {
        if (resetPage) _currentPage = 0;
        // キャッシュはリロード時もクリアしない（ディスク永続化のため蓄積し続ける）

        // 全件ページモード / ソートをリセット（列ヘッダーのインジケータも消す）
        _isAllRecPagedMode = false;
        _allRecPagedPage   = 0;
        _pagedAllRec       = [];
        if (_recActiveSortCol != null && _recOrigHeaders.TryGetValue(_recActiveSortCol, out var sortHeaderOrig))
            _recActiveSortCol.Header = sortHeaderOrig;
        _recActiveSortCol = null;
        _recSortProp      = null;

        _isLoading = true;
        RefreshMenuItem.IsEnabled = false;
        FirstPageButton.IsEnabled = false;
        PrevPageButton.IsEnabled  = false;
        NextPageButton.IsEnabled  = false;
        LastPageButton.IsEnabled  = false;
        Title = "EDCB Viewer  ─  読み込み中...";
        StatusText.Foreground = System.Windows.Media.Brushes.Gray;
        StatusText.Text = "読み込み中...";

        List<RecFileInfo> recList = [];
        List<RecFileInfo> allFromMysql = [];
        List<ReserveData> resList = [];
        string? recErr = null, resErr = null;
        int recTotal = 0;

        var pageSize  = PageSize;
        var pageIndex = _currentPage * pageSize;

        // 録画済み: MySQL recordings テーブルから全件取得
        if (_recordingIndex.IsConfigured)
        {
            try
            {
                allFromMysql = await Task.Run(() => _recordingIndex.LoadAll());
                recTotal = allFromMysql.Count;
                recList  = allFromMysql.Skip(pageIndex).Take(pageSize).ToList();
            }
            catch (Exception ex) { recErr = ex.Message; }
        }

        // 予約: EMWUI から取得
        if (!string.IsNullOrWhiteSpace(_settings.EmwuiBaseUrl))
        {
            var resClient = new EmwuiClient(_settings.EmwuiBaseUrl);
            try { resList = await resClient.GetReserveInfoAsync(); }
            catch (Exception ex) { resErr = ex.Message; }
        }

        // 録画済みは新→旧順（MySQL は ORDER BY start_time DESC 済みだが念のため）
        recList.Sort((a, b) => b.StartTime.CompareTo(a.StartTime));

        // リスト更新前に選択中 ID を退避（更新後に復元）
        var prevRecId = _selectedRec?.ID;
        var prevResId = (ReserveList.SelectedItem as ReserveData)?.ReserveID;

        // 録画済みリストを ICollectionView でラップ（フィルタ + ソートを維持）
        _recList  = recList;
        _recTotal = recTotal;
        var recView = CollectionViewSource.GetDefaultView(_recList);
        recView.Filter = RecFilter;
        if (_recSortProp != null)
            recView.SortDescriptions.Add(new SortDescription(_recSortProp,
                _recSortAsc ? ListSortDirection.Ascending : ListSortDirection.Descending));
        RecInfoList.ItemsSource = recView;
        _recView = recView;

        // 予約リスト
        _resList = resList;
        _resCurrentPage = 0;
        ShowResPage();

        // 選択を復元（ItemsSource 差し替えで消えるため）
        // 録画済み: 同一 ID が新ページにあれば復元、なければ先頭を選択してフォーカス
        var recRestored = false;
        if (prevRecId.HasValue)
        {
            var r = _recList.FirstOrDefault(r => r.ID == prevRecId.Value);
            if (r != null) { RecInfoList.SelectedItem = r; recRestored = true; }
        }
        if (!recRestored && RecInfoList.Items.Count > 0)
        {
            RecInfoList.SelectedIndex = 0;
            RecInfoList.ScrollIntoView(RecInfoList.Items[0]);
        }
        RecInfoList.Focus();

        if (prevResId.HasValue)
        {
            var r = _resList.FirstOrDefault(r => r.ReserveID == prevResId.Value);
            if (r != null) ReserveList.SelectedItem = r;
        }

        _isLoading = false;
        RefreshMenuItem.IsEnabled = true;
        Title = "EDCB Viewer";
        UpdateStatusBar(recErr, resErr);
        UpdatePageControls();
        LastUpdateText.Text = $"最終更新: {DateTime.Now:HH:mm:ss}";

        if (recErr == null)
        {
            _allRecCts.Cancel();
            _allRecCts = new CancellationTokenSource();

            // 全件取得済み。programInfo もロード済みなのでキャッシュに登録するだけ。
            _allRecList  = allFromMysql;
            _pagedAllRec = [];
            foreach (var rec in allFromMysql)
                if (!string.IsNullOrEmpty(rec.ProgramInfo))
                    _programInfoCache.TryAdd(rec.ID, rec.ProgramInfo);
        }
    }


    private void UpdatePageControls()
    {
        var total = TotalPages;
        var hasPrev = _currentPage > 0;
        var hasNext = _currentPage < total - 1;
        FirstPageButton.IsEnabled = hasPrev;
        PrevPageButton.IsEnabled  = hasPrev;
        NextPageButton.IsEnabled  = hasNext;
        LastPageButton.IsEnabled  = hasNext;
        PageLabel.Visibility = Visibility.Visible;
        PageLabel.Text = total > 1 ? $"{_currentPage + 1} / {total} ページ" : "";
        UpdateRecCount();
    }

    /// <summary>ステータスバー左の件数表示を更新する（常時表示）。</summary>
    private void UpdateRecCount()
    {
        var total = _recTotal > 0 ? _recTotal : _recList.Count;
        RecCountText.Text = $"{_recList.Count:#,0} / {total:#,0} 件";
    }

    private void UpdateStatusBar(string? recErr = null, string? resErr = null)
    {
        var err = recErr ?? resErr;
        if (err != null)
        {
            var url = _settings.EmwuiBaseUrl;
            StatusText.Text = $"API エラー: {err}";
            StatusText.Foreground = System.Windows.Media.Brushes.Tomato;
            // 初回エラー時はポップアップでも通知（再試行ループで何度も出ないよう _lastErrMsg で抑制）
            if (err != _lastErrMsg)
            {
                _lastErrMsg = err;
                MessageBox.Show(
                    $"EMWUI API への接続に失敗しました。\n\n{err}\n\nURL: {url}\n\n" +
                    $"設定で正しい URL（例: http://5600x:5510）を入力してください。\n" +
                    $"ブラウザで {url}/api/EnumRecInfo?count=1 を開いて XML が返るか確認できます。",
                    "接続エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return;
        }
        _lastErrMsg = null;
        StatusText.Foreground = System.Windows.Media.Brushes.Gray;
        StatusText.Text = "";
        UpdateRecCount();
    }

    // ─── フィルター ──────────────────────────────────────────────────────────

    /// <summary>
    /// フィールド文字列から全角・半角スペースを除去して返す。
    /// データ側のスペース揺れ（「古賀葵」「古賀 葵」「古賀　葵」）を吸収するために
    /// 比較対象フィールドに適用する。クエリ側には適用しない（AND 分割用スペースを保持するため）。
    /// </summary>
    // 全角スペース（U+3000）のみ除去。半角スペースは AND 区切りとして保持するため触らない。
    // 「古賀葵」と「古賀　葵」のデータ揺れを吸収しつつ「NHK BS」→「NHKBS」の誤ヒットを防ぐ。
    private static string StripFwSpace(string s) => s.Replace("　", "");

    // RecFilter 用の正規表現キャッシュ（CollectionView.Refresh で全件呼ばれるため毎回生成しない）
    private Regex? _recFilterRegex;
    private string? _recFilterRegexPattern;

    private bool RecFilter(object obj)
    {
        if (obj is not RecFileInfo info) return false;
        var q = RecSearchBox.Text.Trim();
        if (string.IsNullOrEmpty(q)) return true;
        if (TryParseDateFilter(q, out var op, out var date))
            return MatchesDate(info.StartTime, op, date);

        // ── 正規表現モード ──
        if (RegexCheck.IsChecked == true)
        {
            if (_recFilterRegexPattern != q)
            {
                _recFilterRegexPattern = q;
                try { _recFilterRegex = new Regex(q, RegexOptions.IgnoreCase | RegexOptions.Singleline); }
                catch { _recFilterRegex = null; }
            }
            if (_recFilterRegex == null) return false;
            return _recFilterRegex.IsMatch(info.Title)
                || _recFilterRegex.IsMatch(info.ServiceName)
                || _recFilterRegex.IsMatch(info.ProgramInfo)
                || _recFilterRegex.IsMatch(info.Comment);
        }

        // ── 通常モード: 半角スペースで AND 分割、全角スペースはクエリ/フィールド両側から除去 ──
        var tokens = q.Replace("　", " ")
                      .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var titleN   = StripFwSpace(info.Title);
        var serviceN = StripFwSpace(info.ServiceName);
        var progN    = StripFwSpace(info.ProgramInfo);
        var commentN = StripFwSpace(info.Comment);
        bool Match(string field) => tokens.All(t => field.Contains(t, StringComparison.OrdinalIgnoreCase));
        return Match(titleN) || Match(serviceN) || Match(progN) || Match(commentN);
    }

    private bool ResFilter(object obj)
    {
        if (obj is not ReserveData data) return false;
        var q = ResSearchBox.Text.Trim();
        if (string.IsNullOrEmpty(q)) return true;
        if (TryParseDateFilter(q, out var op, out var date))
            return MatchesDate(data.StartTime, op, date);
        return data.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
            || data.StationName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || (data.ProgramInfo?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static bool MatchesDate(DateTime startTime, string op, DateTime date)
    {
        var d = startTime.Date;
        return op switch
        {
            "<=" => d <= date,
            ">=" => d >= date,
            "<"  => d <  date,
            ">"  => d >  date,
            _    => d == date,
        };
    }

    /// <summary>
    /// "&lt;=2026/05/06" のような日付フィルタ文字列を分解する。
    /// 演算子で始まるが日付として解釈できない場合は false（テキスト検索にフォールバックしない）。
    /// </summary>
    private static bool TryParseDateFilter(string query, out string op, out DateTime date)
    {
        op = "";
        date = default;

        // 2文字演算子を先にチェックしないと "<" が "<=" に勝ってしまう
        foreach (var o in (ReadOnlySpan<string>)["<=", ">=", "<", ">", "="])
        {
            if (!query.StartsWith(o)) continue;
            var rest = query[o.Length..].Trim();
            if (TryParseDate(rest, out date))
            {
                op = o;
                return true;
            }
            return false;   // 演算子はあるが日付が不正 → マッチなしで処理
        }
        return false;
    }

    // 受け付ける日付フォーマット（年省略形は当年として解釈される）
    private static readonly string[] _dateFmts =
        ["yyyy/MM/dd", "yyyy-MM-dd", "yyyy/M/d", "yyyy-M-d",
         "MM/dd", "M/d", "MM-dd", "M-d"];

    private static bool TryParseDate(string s, out DateTime date)
    {
        // 年省略形（MM/dd など）は当年を補完して再試行
        if (DateTime.TryParseExact(s, _dateFmts,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out date))
            return true;

        // "05/06" など年なし → 今年を付けて再パース
        if (s.Length <= 5 &&
            DateTime.TryParseExact($"{DateTime.Today.Year}/{s}", ["yyyy/MM/dd", "yyyy/M/d"],
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out date))
            return true;

        return false;
    }

    private void RecSearch_Click(object sender, RoutedEventArgs e)
        => ApplySearchMode();

    private void RecSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        ApplySearchMode();
        FocusListFirst(RecInfoList);
    }

    private static void FocusListFirst(ListView list)
    {
        if (list.Items.Count > 0)
            list.SelectedIndex = 0;
        list.Focus();
    }

    // TextChanged はクリア時（空になった瞬間）のみページビューへ戻す
    private void RecSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(RecSearchBox.Text)) ApplySearchMode();
    }

    private void FullTextCheck_Changed(object sender, RoutedEventArgs e)
    {
        // チェック変更だけでは発火しない。ボタン/Enter で明示的に再検索する
    }

    private void RegexCheck_Changed(object sender, RoutedEventArgs e)
    {
        // チェック変更だけでは発火しない。ボタン/Enter で明示的に再検索する
    }

    /// <summary>
    /// 検索ボックスの内容に応じてビューを切り替える。
    /// 空かつソートなし → ページビューに戻す
    /// 空かつソートあり → ソートのみの全件ページモードに戻す
    /// 有り → 全件をフィルタ＋ソートしてページング表示
    /// </summary>
    private void ApplySearchMode()
    {
        var q = RecSearchBox.Text.Trim();
        if (string.IsNullOrEmpty(q))
        {
            if (_recSortProp != null && _allRecList.Count > 0)
            {
                // ソートが有効 → ソートのみの全件ページモードに戻す
                _allRecPagedPage = 0;
                EnterAllRecPagedMode();
                StatusText.Text = "";
                return;
            }
            // 検索もソートもなし → サーバーページビューに戻す
            _isAllRecPagedMode = false;
            RecInfoList.ItemsSource = _recView;
            PageLabel.Visibility = Visibility.Visible;
            UpdatePageControls();
            StatusText.Text = "";
            return;
        }

        if (_allRecList.Count == 0)
        {
            // 全件未取得 → 取得完了時に再適用
            StatusText.Text = "検索用データ取得中...";
            return;
        }

        _allRecPagedPage = 0;
        EnterAllRecPagedMode();
    }

    private void ResSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(ResSearchBox.Text))
        {
            _resCurrentPage = 0;
            ShowResPage();
        }
    }

    private void ResSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _resCurrentPage = 0;
        ShowResPage();
        FocusListFirst(ReserveList);
    }

    // ─── 列ヘッダークリック（ソート）────────────────────────────────────────

    private void RecInfoList_ColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header || header.Column == null) return;
        var col = header.Column;
        var origText = _recOrigHeaders.TryGetValue(col, out var t) ? t : col.Header?.ToString() ?? "";
        if (!RecSortProps.TryGetValue(origText, out var prop)) return;

        // ヘッダーインジケータ ▲▼ とソート状態を更新（_pagedAllRec への適用は EnterAllRecPagedMode で行う）
        ApplySort(col, prop, ref _recActiveSortCol, ref _recSortProp, ref _recSortAsc,
                  _recOrigHeaders, null);

        _allRecPagedPage = 0;
        EnterAllRecPagedMode();
    }

    /// <summary>
    /// _allRecList を現在の検索クエリでフィルタし、_recSortProp でソートして
    /// _pagedAllRec に格納し、全件ページモードで表示する。
    /// ソートも検索も両方ここで処理する統合エントリ。
    /// </summary>
    private void EnterAllRecPagedMode()
    {
        if (_allRecList.Count == 0) return;

        var q = RecSearchBox.Text.Trim();

        // ① フィルタ
        IEnumerable<RecFileInfo> items = _allRecList;
        if (!string.IsNullOrEmpty(q))
        {
            if (TryParseDateFilter(q, out var op, out var date))
            {
                items = items.Where(r => MatchesDate(r.StartTime, op, date));
            }
            else if (RegexCheck.IsChecked == true)
            {
                // ── 正規表現モード ──
                Regex? rx = null;
                try
                {
                    rx = new Regex(q, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
                }
                catch (ArgumentException ex)
                {
                    StatusText.Text = $"正規表現エラー: {ex.Message}";
                    StatusText.Foreground = System.Windows.Media.Brushes.Orange;
                    _pagedAllRec = [];
                    _isAllRecPagedMode = true;
                    ShowAllRecPage();
                    return;
                }
                items = items.Where(r =>
                    rx.IsMatch(r.Title)
                    || rx.IsMatch(r.ServiceName)
                    || rx.IsMatch(r.Comment)
                    || (FullTextCheck.IsChecked == true
                        && _programInfoCache.TryGetValue(r.ID, out var prog)
                        && !string.IsNullOrEmpty(prog)
                        && rx.IsMatch(prog)));
            }
            else
            {
                // ── 通常モード: 半角スペースで AND 分割、全角スペースは両側から除去 ──
                var tokens = q.Replace("　", " ")
                              .Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length > 1)
                {
                    // フレーズ優先: トークン連結形 ("古賀葵") がデータ内に存在すればフレーズモード
                    var phrase = string.Concat(tokens);
                    bool phraseExists = items.Any(r =>
                    {
                        var tN = StripFwSpace(r.Title);
                        var sN = StripFwSpace(r.ServiceName);
                        var cN = StripFwSpace(r.Comment);
                        _programInfoCache.TryGetValue(r.ID, out var p2);
                        var pN = StripFwSpace(p2 ?? r.ProgramInfo);
                        bool HP(string f) => f.Contains(phrase, StringComparison.OrdinalIgnoreCase)
                                          || f.Contains(q,      StringComparison.OrdinalIgnoreCase);
                        return HP(tN) || HP(sN) || HP(cN) || HP(pN);
                    });
                    if (phraseExists)
                    {
                        // フレーズモード: 連結形またはスペースあり形が含まれるもののみ（AND は使わない）
                        items = items.Where(r =>
                        {
                            var tN = StripFwSpace(r.Title);
                            var sN = StripFwSpace(r.ServiceName);
                            var cN = StripFwSpace(r.Comment);
                            _programInfoCache.TryGetValue(r.ID, out var p2);
                            var pN = StripFwSpace(p2 ?? r.ProgramInfo);
                            bool HP(string f) => f.Contains(phrase, StringComparison.OrdinalIgnoreCase)
                                              || f.Contains(q,      StringComparison.OrdinalIgnoreCase);
                            return HP(tN) || HP(sN) || HP(cN) || HP(pN);
                        });
                    }
                    else
                    {
                        // AND モード: 全トークンが同一フィールド内に揃っている場合のみヒット
                        items = items.Where(r =>
                        {
                            var tN = StripFwSpace(r.Title);
                            var sN = StripFwSpace(r.ServiceName);
                            var cN = StripFwSpace(r.Comment);
                            _programInfoCache.TryGetValue(r.ID, out var p2);
                            var pN = StripFwSpace(p2 ?? r.ProgramInfo);
                            bool Match(string f) => tokens.All(t => f.Contains(t, StringComparison.OrdinalIgnoreCase));
                            return Match(tN) || Match(sN) || Match(cN) || Match(pN);
                        });
                    }
                }
                else
                {
                    items = items.Where(r =>
                    {
                        var tN = StripFwSpace(r.Title);
                        var sN = StripFwSpace(r.ServiceName);
                        var cN = StripFwSpace(r.Comment);
                        _programInfoCache.TryGetValue(r.ID, out var p2);
                        var pN = StripFwSpace(p2 ?? r.ProgramInfo);
                        return tN.Contains(tokens[0], StringComparison.OrdinalIgnoreCase)
                            || sN.Contains(tokens[0], StringComparison.OrdinalIgnoreCase)
                            || cN.Contains(tokens[0], StringComparison.OrdinalIgnoreCase)
                            || pN.Contains(tokens[0], StringComparison.OrdinalIgnoreCase);
                    });
                }
            }
        }

        _pagedAllRec = items.ToList();

        // ② ソート
        if (_recSortProp != null)
        {
            var asc = _recSortAsc;
            Comparison<RecFileInfo> cmp = _recSortProp switch
            {
                "Title"          => (a, b) => string.Compare(a.Title,       b.Title,       StringComparison.CurrentCulture),
                "ServiceName"    => (a, b) => string.Compare(a.ServiceName, b.ServiceName, StringComparison.CurrentCulture),
                "StartTime"      => (a, b) => a.StartTime.CompareTo(b.StartTime),
                "DurationSecond" => (a, b) => a.DurationSecond.CompareTo(b.DurationSecond),
                "RecStatus"      => (a, b) => a.RecStatus.CompareTo(b.RecStatus),
                _                => (a, b) => a.StartTime.CompareTo(b.StartTime),
            };
            _pagedAllRec.Sort(asc ? cmp : (a, b) => -cmp(a, b));
        }

        _isAllRecPagedMode = true;
        ShowAllRecPage();
    }

    /// <summary>全件ページモードの現在ページ (_allRecPagedPage) を表示する。</summary>
    private void ShowAllRecPage()
    {
        var pageItems = _pagedAllRec
            .Skip(_allRecPagedPage * PageSize)
            .Take(PageSize)
            .ToList();

        RecInfoList.ItemsSource = CollectionViewSource.GetDefaultView(pageItems);

        var total   = AllRecPagedTotalPages;
        var hasPrev = _allRecPagedPage > 0;
        var hasNext = _allRecPagedPage < total - 1;
        FirstPageButton.IsEnabled = hasPrev;
        PrevPageButton.IsEnabled  = hasPrev;
        NextPageButton.IsEnabled  = hasNext;
        LastPageButton.IsEnabled  = hasNext;
        PageLabel.Visibility = Visibility.Visible;
        PageLabel.Text = total > 1 ? $"{_allRecPagedPage + 1} / {total} ページ" : "";

        var rangeStart = _allRecPagedPage * PageSize + 1;
        var rangeEnd   = _allRecPagedPage * PageSize + pageItems.Count;
        RecCountText.Text = $"{rangeStart:#,0} ～ {rangeEnd:#,0} / {_pagedAllRec.Count:#,0} 件";

        // 先頭を自動選択してリストにフォーカス
        if (RecInfoList.Items.Count > 0)
        {
            RecInfoList.SelectedIndex = 0;
            RecInfoList.ScrollIntoView(RecInfoList.Items[0]);
        }
        RecInfoList.Focus();
    }

    private void ReserveList_ColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header || header.Column == null) return;
        var col = header.Column;
        var origText = _resOrigHeaders.TryGetValue(col, out var t) ? t : col.Header?.ToString() ?? "";
        if (!ResSortProps.TryGetValue(origText, out var prop)) return;

        ApplySort(col, prop, ref _resActiveSortCol, ref _resSortProp, ref _resSortAsc,
                  _resOrigHeaders, null);
        _resCurrentPage = 0;
        ShowResPage();
    }

    /// <summary>
    /// ソートを適用し、列ヘッダーに ▲/▼ インジケータを表示する。
    /// 同じ列を再度クリックすると昇順/降順をトグルする。
    /// </summary>
    private static void ApplySort(
        GridViewColumn col,
        string prop,
        ref GridViewColumn? activeCol,
        ref string? sortProp,
        ref bool sortAsc,
        Dictionary<GridViewColumn, string> origHeaders,
        ICollectionView? view)
    {
        if (sortProp == prop)
        {
            // 同一列 → 昇降順トグル
            sortAsc = !sortAsc;
        }
        else
        {
            // 別の列 → 前の列のインジケータをリセット
            if (activeCol != null && origHeaders.TryGetValue(activeCol, out var prev))
                activeCol.Header = prev;
            sortProp = prop;
            sortAsc = true;
        }

        activeCol = col;
        var origText = origHeaders.TryGetValue(col, out var t) ? t : prop;
        col.Header = $"{origText} {(sortAsc ? "▲" : "▼")}";

        view?.SortDescriptions.Clear();
        view?.SortDescriptions.Add(new SortDescription(prop,
            sortAsc ? ListSortDirection.Ascending : ListSortDirection.Descending));
    }

    // ─── 録画済み選択 / ダブルクリック ──────────────────────────────────────

    private void RecInfoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecInfoList.SelectedItem is not RecFileInfo info)
        {
            ClearRecPanel();
            return;
        }

        _selectedRec = info;
        RecTitle.Text    = info.Title;
        RecService.Text  = info.ServiceName;
        RecDateTime.Text = $"{info.StartTime:yyyy/MM/dd HH:mm} ({info.DurationText})";
        RecFilePath.Text = _settings.ToUncPath(info.RecFilePath);
        var errSuffix = string.IsNullOrWhiteSpace(info.ErrInfo) ? "" : $"  [{info.ErrInfo.Trim()}]";
        RecDrops.Text    = $"ドロップ: {info.Drops}  スクランブル: {info.Scrambles}{errSuffix}";
        PlayButton.Visibility = Visibility.Visible;

        // ── 番組情報: MySQL program_info → EpgDB events テーブルの順で取得 ──
        if (_programInfoCache.TryGetValue(info.ID, out var cached) && !string.IsNullOrEmpty(cached))
        {
            ShowProgramInfo(cached);
            return;
        }
        if (!string.IsNullOrEmpty(info.ProgramInfo))
        {
            ShowProgramInfo(info.ProgramInfo);
            return;
        }

        if (info.EventID != 0)
        {
            var epgText = new EpgDbReader(_settings.DbConnectionString).GetEventInfoText(
                info.OriginalNetworkID, info.TransportStreamID, info.ServiceID, info.EventID);
            if (!string.IsNullOrEmpty(epgText))
            {
                _programInfoCache[info.ID] = epgText;
                ShowProgramInfo(epgText);
                return;
            }
        }

        ShowProgramInfo("");
    }

    private void ShowProgramInfo(string text)
    {
        var vis = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        RecProgramInfoLabel.Visibility = vis;
        RecProgramInfo.Visibility      = vis;
        RecProgramInfo.Text            = text;
    }

    private void RecInfoList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RecInfoList.SelectedItem is RecFileInfo info)
            OpenWithPlayer(_settings.ToUncPath(info.RecFilePath));
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRec != null)
            OpenWithPlayer(_settings.ToUncPath(_selectedRec.RecFilePath));
    }

    // ─── 予約選択 / ダブルクリック ───────────────────────────────────────────

    private void ReserveList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ReserveList.SelectedItem is not ReserveData reserve)
            return;

        var match = _recList.FirstOrDefault(r =>
            r.Title == reserve.Title && r.StartTime == reserve.StartTime)
            ?? _recList
                .Where(r => r.Title == reserve.Title)
                .OrderByDescending(r => r.StartTime)
                .FirstOrDefault();

        if (match != null)
        {
            OpenWithPlayer(_settings.ToUncPath(match.RecFilePath));
            return;
        }

        if (reserve.IsRecording)
        {
            var liveFile = FindRecordingFile(reserve);
            if (liveFile != null)
            {
                OpenWithPlayer(liveFile, skipExistCheck: true);
                return;
            }
            StatusText.Text = "録画中ファイルが見つかりません — 録画開始直後は数秒お待ちください";
        }
        else
        {
            StatusText.Text = "録画ファイル未検出 — 録画終了後に更新してください";
        }
    }

    private string? FindRecordingFile(ReserveData reserve)
    {
        var folder = _settings.RecordingFolder;
        if (!Directory.Exists(folder))
            return null;

        try
        {
            var margin = TimeSpan.FromMinutes(5);
            var earliest = reserve.StartTime - margin;
            var latest = reserve.StartTime + margin;

            return Directory
                .EnumerateFiles(folder, "*.ts", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(folder, "*.m2ts", SearchOption.TopDirectoryOnly))
                .Select(f => new FileInfo(f))
                .Where(fi => fi.CreationTime >= earliest && fi.CreationTime <= latest)
                .OrderByDescending(fi => fi.LastWriteTime)
                .FirstOrDefault()?.FullName;
        }
        catch
        {
            return null;
        }
    }

    private void OpenWithPlayer(string path, bool skipExistCheck = false)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (!File.Exists(_settings.PlayerPath))
        {
            MessageBox.Show($"MPC-BE が見つかりません:\n{_settings.PlayerPath}\n\n設定でパスを変更してください。",
                "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!skipExistCheck && !File.Exists(path))
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

    private void ClearRecPanel()
    {
        _selectedRec = null;
        RecTitle.Text = "";
        RecService.Text = "";
        RecDateTime.Text = "";
        RecFilePath.Text = "";
        RecDrops.Text = "";
        RecProgramInfo.Text = "";
        RecProgramInfoLabel.Visibility = Visibility.Collapsed;
        RecProgramInfo.Visibility      = Visibility.Collapsed;
        PlayButton.Visibility = Visibility.Collapsed;
    }

    private async void ReserveList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReserveList.SelectedItem is not ReserveData data)
        {
            ClearResPanel();
            return;
        }
        ResTitle.Text = data.Title;
        ResStation.Text = data.StationName;
        ResDateTime.Text = $"{data.StartTime:yyyy/MM/dd HH:mm} ～ {data.EndTimeText} ({data.DurationText})";
        ResStatus.Text = data.StatusText;
        ResComment.Text = data.Comment;

        // 番組情報: null = 未取得 → 個別取得、"" = 取得済みで空 → スキップ
        if (data.ProgramInfo is null)
        {
            var text = "";
            if (data.EventID != 0)
            {
                // EpgData.db から取得
                text = new EpgDbReader(_settings.DbConnectionString).GetEventInfoText(
                    data.OriginalNetworkID, data.TransportStreamID,
                    data.ServiceID, data.EventID) ?? "";

                // EMWUI フォールバック
                if (string.IsNullOrEmpty(text) && !string.IsNullOrWhiteSpace(_settings.EmwuiBaseUrl))
                {
                    text = await new EmwuiClient(_settings.EmwuiBaseUrl).GetEventInfoTextAsync(
                        data.OriginalNetworkID, data.TransportStreamID,
                        data.ServiceID, data.EventID);
                }
            }
            data.ProgramInfo = text;
            if (ReserveList.SelectedItem != data) return;
        }

        var hasInfo = !string.IsNullOrEmpty(data.ProgramInfo);
        ResProgramInfo.Text = data.ProgramInfo ?? "";
        ResProgramInfoLabel.Visibility = hasInfo ? Visibility.Visible : Visibility.Collapsed;
        ResProgramInfo.Visibility = ResProgramInfoLabel.Visibility;
    }

    private void ClearResPanel()
    {
        ResTitle.Text = "";
        ResStation.Text = "";
        ResDateTime.Text = "";
        ResStatus.Text = "";
        ResComment.Text = "";
        ResProgramInfo.Text = "";
        ResProgramInfoLabel.Visibility = Visibility.Collapsed;
        ResProgramInfo.Visibility = Visibility.Collapsed;
    }

    private void MainTab_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, MainTab)) return;
        if (MainTab.SelectedItem is TabItem { Header: "ディレクトリ" })
        {
            var root = _settings.EncodedFolder;
            var rootNorm = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (_dirRoot != rootNorm || string.IsNullOrEmpty(_currentDirPath))
            {
                _dirRoot = rootNorm;
                _currentDirPath = rootNorm;
            }
            LoadMediaFiles();
        }
    }

    // ─── ディレクトリタブ ─────────────────────────────────────────────────────

    private bool _dirLoading = false;
    private string _dirRoot = "";
    private string _currentDirPath = "";

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
        try
        {
            await LoadMediaFilesCore();
        }
        finally
        {
            _dirLoading = false;
        }
    }

    private async Task LoadMediaFilesCore()
    {
        var folder = _currentDirPath;
        if (string.IsNullOrWhiteSpace(folder))
        {
            _mediaFiles = [];
            DirList.ItemsSource = null;
            StatusText.Text = "エンコード済みフォルダが未設定です。設定 → パス設定... で指定してください。";
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

        ApplySort(col, prop, ref _dirActiveSortCol, ref _dirSortProp, ref _dirSortAsc,
                  _dirOrigHeaders, null);
        _dirCurrentPage = 0;
        ShowDirPage();
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
            ? $"{total:#,0} 件のファイルが見つかりました"
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
        FocusListFirst(DirList);
    }

    private void DirRefresh_Click(object sender, RoutedEventArgs e) => LoadMediaFiles();

    private void DirList_SelectionChanged(object sender, SelectionChangedEventArgs e)
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

        RecordingIndexEntry? entry;
        try { entry = _recordingIndex.Find(file.FileName); }
        catch { entry = null; }

        if (entry == null)
        {
            DirTitle.Text = file.ParsedTitle;
            DirService.Text = file.ParsedStation;
            DirDateTime.Text = file.ParsedStartTimeText;
            DirDrops.Text = "";
            DirProgramInfoLabel.Visibility = Visibility.Collapsed;
            DirProgramInfo.Visibility = Visibility.Collapsed;
            return;
        }

        DirTitle.Text = entry.FullTitle;
        DirService.Text = entry.ServiceName;
        DirDateTime.Text = $"{entry.StartTime:yyyy/MM/dd HH:mm} ({entry.DurationText})";
        var errSuffix = string.IsNullOrWhiteSpace(entry.Comment) ? "" : $"  [{entry.Comment.Trim()}]";
        DirDrops.Text = $"ドロップ: {entry.Drops}  スクランブル: {entry.Scrambles}{errSuffix}";

        var hasInfo = !string.IsNullOrEmpty(entry.ProgramInfo);
        DirProgramInfoLabel.Visibility = hasInfo ? Visibility.Visible : Visibility.Collapsed;
        DirProgramInfo.Visibility = DirProgramInfoLabel.Visibility;
        DirProgramInfo.Text = entry.ProgramInfo;
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
        {
            _dirRoot = selected.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
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

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

    /// <summary>
    /// PreviewKeyDown（トンネリング）で ListView より先にページ移動キーを処理する。
    /// ListView は PageDown/Home/End をスクロール・選択移動として消費するため、
    /// KeyDown（バブリング）まで届かない。PreviewKeyDown なら確実に先取りできる。
    /// </summary>
    // ─── 予約録画ページング ───────────────────────────────────────────────────

    private int _resCurrentPage = 0;
    private int ResTotalPages(int count) => Math.Max(1, (count + PageSize - 1) / PageSize);

    private List<ReserveData> GetResList()
    {
        var filtered = _resList.Where(r => ResFilter(r)).ToList();
        if (_resSortProp != null)
        {
            var asc = _resSortAsc;
            Comparison<ReserveData> cmp = _resSortProp switch
            {
                "Title"          => (a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCulture),
                "StationName"    => (a, b) => string.Compare(a.StationName, b.StationName, StringComparison.CurrentCulture),
                "StartTime"      => (a, b) => a.StartTime.CompareTo(b.StartTime),
                "DurationSecond" => (a, b) => a.DurationSecond.CompareTo(b.DurationSecond),
                "ReserveStatus"  => (a, b) => a.ReserveStatus.CompareTo(b.ReserveStatus),
                _                => (a, b) => a.StartTime.CompareTo(b.StartTime),
            };
            filtered.Sort(asc ? cmp : (a, b) => -cmp(a, b));
        }
        return filtered;
    }

    private void ShowResPage()
    {
        var list = GetResList();
        var total = list.Count;
        var totalPages = ResTotalPages(total);
        _resCurrentPage = Math.Clamp(_resCurrentPage, 0, totalPages - 1);

        ReserveList.ItemsSource = list.Skip(_resCurrentPage * PageSize).Take(PageSize).ToList();

        var hasPrev = _resCurrentPage > 0;
        var hasNext = _resCurrentPage < totalPages - 1;
        ResFirstPageButton.IsEnabled = hasPrev;
        ResPrevPageButton.IsEnabled  = hasPrev;
        ResNextPageButton.IsEnabled  = hasNext;
        ResLastPageButton.IsEnabled  = hasNext;
        ResPageLabel.Text = totalPages > 1 ? $"{_resCurrentPage + 1} / {totalPages} ページ" : "";
        ResCountText.Text = $"{total:#,0} 件";
    }

    private void ResFirstPage_Click(object sender, RoutedEventArgs e) { _resCurrentPage = 0; ShowResPage(); }
    private void ResPrevPage_Click(object sender, RoutedEventArgs e)  { _resCurrentPage--; ShowResPage(); }
    private void ResNextPage_Click(object sender, RoutedEventArgs e)  { _resCurrentPage++; ShowResPage(); }
    private void ResLastPage_Click(object sender, RoutedEventArgs e)  { _resCurrentPage = int.MaxValue; ShowResPage(); }

    private string ActiveTab => (MainTab.SelectedItem as TabItem)?.Header?.ToString() ?? "";

    private TextBox? GetCurrentSearchBox() => ActiveTab switch
    {
        "録画済み"     => RecSearchBox,
        "予約録画"     => ResSearchBox,
        "ディレクトリ" => DirSearchBox,
        _              => null,
    };


    // PreviewTextInput はテキスト確定時に発火するため、イベントルーティング中の Focus() による
    // WPF 不正状態を避けつつ最初の1文字を確実に検索ボックスへ届けられる。
    private void Window_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox tb && !tb.IsReadOnly) return;
        var searchBox = GetCurrentSearchBox();
        if (searchBox == null) return;
        searchBox.Focus();
        searchBox.AppendText(e.Text);
        searchBox.CaretIndex = searchBox.Text.Length;
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox tb && !tb.IsReadOnly) return;

        var tab = ActiveTab;
        switch (e.Key)
        {
            case Key.PageDown:
                if      (tab == "ディレクトリ") DirNextPage_Click(this, new RoutedEventArgs());
                else if (tab == "予約録画")     ResNextPage_Click(this, new RoutedEventArgs());
                else                            NextPage_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.PageUp:
                if      (tab == "ディレクトリ") DirPrevPage_Click(this, new RoutedEventArgs());
                else if (tab == "予約録画")     ResPrevPage_Click(this, new RoutedEventArgs());
                else                            PrevPage_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.Home:
                if      (tab == "ディレクトリ") DirFirstPage_Click(this, new RoutedEventArgs());
                else if (tab == "予約録画")     ResFirstPage_Click(this, new RoutedEventArgs());
                else                            FirstPage_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.End:
                if      (tab == "ディレクトリ") DirLastPage_Click(this, new RoutedEventArgs());
                else if (tab == "予約録画")     ResLastPage_Click(this, new RoutedEventArgs());
                else                            LastPage_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
        }
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        SaveProgInfoCache();
        _recordingIndex.Dispose();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        var isEditable = Keyboard.FocusedElement is TextBox tb && !tb.IsReadOnly;
        var tab = ActiveTab;

        switch (e.Key)
        {
            case Key.D1 when e.KeyboardDevice.Modifiers == ModifierKeys.Control:
                MainTab.SelectedIndex = 0;
                e.Handled = true;
                break;
            case Key.D2 when e.KeyboardDevice.Modifiers == ModifierKeys.Control:
                MainTab.SelectedIndex = 1;
                e.Handled = true;
                break;
            case Key.D3 when e.KeyboardDevice.Modifiers == ModifierKeys.Control:
                MainTab.SelectedIndex = 2;
                e.Handled = true;
                break;
            case Key.F5:
                if (tab == "ディレクトリ") LoadMediaFiles();
                else Reload();
                e.Handled = true;
                break;
            case Key.Enter when !isEditable:
                if (tab == "録画済み" && _selectedRec != null)
                    OpenWithPlayer(_settings.ToUncPath(_selectedRec.RecFilePath));
                else if (tab == "ディレクトリ" && _selectedMediaFile != null)
                {
                    if (_selectedMediaFile.IsDirectory) NavigateDir(_selectedMediaFile.FilePath);
                    else OpenWithPlayer(_selectedMediaFile.FilePath);
                }
                else if (tab == "予約録画")
                    ReserveList_DoubleClick(this, null!);
                e.Handled = true;
                break;
            case Key.Up when !isEditable:
                if (tab == "ディレクトリ") MoveDirListSelection(-1);
                else if (tab == "録画済み") MoveRecListSelection(-1);
                else MoveResListSelection(-1);
                e.Handled = true;
                break;
            case Key.Down when !isEditable:
                if (tab == "ディレクトリ") MoveDirListSelection(+1);
                else if (tab == "録画済み") MoveRecListSelection(+1);
                else MoveResListSelection(+1);
                e.Handled = true;
                break;
        }
    }

    private void MoveRecListSelection(int delta)
    {
        var count = RecInfoList.Items.Count;
        if (count == 0) return;
        var next = Math.Clamp(RecInfoList.SelectedIndex + delta, 0, count - 1);
        RecInfoList.SelectedIndex = next;
        RecInfoList.ScrollIntoView(RecInfoList.SelectedItem);
    }

    private void MoveResListSelection(int delta)
    {
        var count = ReserveList.Items.Count;
        if (count == 0) return;
        var next = Math.Clamp(ReserveList.SelectedIndex + delta, 0, count - 1);
        ReserveList.SelectedIndex = next;
        ReserveList.ScrollIntoView(ReserveList.SelectedItem);
    }

    private void MoveDirListSelection(int delta)
    {
        var count = DirList.Items.Count;
        if (count == 0) return;
        var next = Math.Clamp(DirList.SelectedIndex + delta, 0, count - 1);
        DirList.SelectedIndex = next;
        DirList.ScrollIntoView(DirList.SelectedItem);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow(_settings);
        if (win.ShowDialog() == true)
        {
            _settings = win.Settings;
            _settings.Save();
            // 設定保存だけではリロードしない。F5 または「更新」で手動取得。
            StatusText.Text = "設定を保存しました。F5 または「更新」で再読み込みできます。";
        }
    }

}
