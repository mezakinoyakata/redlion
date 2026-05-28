using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using EDCBViewer.Models;
using EDCBViewer.Parsers;
using EDCBViewer.Services;

namespace EDCBViewer;

public partial class MainWindow : Window
{
    private AppSettings _settings = AppSettings.Load();
    private PlayServer? _playServer;
    private RecFileInfo? _selectedRec;
    private List<RecFileInfo> _recList = [];    // 現在ページ分
    private List<RecFileInfo> _allRecList = []; // 検索用全件（バックグラウンド取得）
    private List<ReserveData> _resList = [];

    // フィルタ + ソート用 CollectionView
    private ICollectionView? _recView;    // ページビュー
    private ICollectionView? _resView;

    // ソート状態
    private string? _recSortProp;
    private bool _recSortAsc = true;
    private GridViewColumn? _recActiveSortCol;

    // 全件ページモード（ソート or 検索 → _allRecList ベースのページング）
    private bool _isAllRecPagedMode = false;  // 全件ページモード中
    private bool _sortModePending   = false;  // 全件未取得時の全件ページモード予約
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
        InitSortHeaders();
        StartPlayServer();
        // 自動リロードは重いため無効化。手動更新（メニュー「更新」）のみ。
        Loaded += (_, _) => Reload();
        Closed += (_, _) => _playServer?.Stop();
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
    }

    private bool _isLoading;
    private int  _recTotal;                           // サーバー上の全録画件数
    private int  _currentPage;                        // 現在のページ（0始まり）
    private CancellationTokenSource _epgCts    = new(); // 右ペイン番組情報取得
    private CancellationTokenSource _bgCts     = new(); // バックグラウンド全文取得（現ページ）
    private CancellationTokenSource _allRecCts = new(); // 全件検索リスト取得

    // 全文検索用キャッシュ: ID → programInfo テキスト
    // _recList と _allRecList は別オブジェクトのため共有辞書で橋渡し。
    // バックグラウンドスレッドと UI スレッドが同時アクセスするため ConcurrentDictionary を使用。
    private readonly ConcurrentDictionary<uint, string> _programInfoCache = new();

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
        if (_isLoading) return;                         // 連打ガード
        if (string.IsNullOrWhiteSpace(_settings.EmwuiBaseUrl))
        {
            StatusText.Text = "EMWUI URL が未設定です。設定 → パス設定... で URL を入力してください。";
            return;
        }

        if (resetPage) _currentPage = 0;
        // F5・設定変更時はキャッシュを全クリア。ページ移動時は蓄積したキャッシュを保持する
        if (resetPage) _programInfoCache.Clear();

        // 全件ページモード / ソートをリセット（列ヘッダーのインジケータも消す）
        _isAllRecPagedMode = false;
        _sortModePending   = false;
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
        List<ReserveData> resList = [];
        string? recErr = null, resErr = null;
        int recTotal = 0;

        var client = new EmwuiClient(_settings.EmwuiBaseUrl);
        var pageSize = PageSize;
        var pageIndex = _currentPage * pageSize;

        // 録画済み・予約を並列取得（Task.Run 不要: HttpClient は元からスレッドセーフ非同期）
        var recTask = client.GetRecFileInfoAsync(pageSize, pageIndex);
        var resTask = client.GetReserveInfoAsync();

        try { (recList, recTotal) = await recTask; }
        catch (Exception ex) { recErr = ex.Message; }

        try { resList = await resTask; }
        catch (Exception ex) { resErr = ex.Message; }

        // 録画済みは新→旧順（念のためソート）
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

        // 予約リストを ICollectionView でラップ
        _resList = resList;
        var resView = CollectionViewSource.GetDefaultView(_resList);
        resView.Filter = ResFilter;
        if (_resSortProp != null)
            resView.SortDescriptions.Add(new SortDescription(_resSortProp,
                _resSortAsc ? ListSortDirection.Ascending : ListSortDirection.Descending));
        ReserveList.ItemsSource = resView;
        _resView = resView;

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
            // ① 現ページの番組情報をバックグラウンドで取得（全文検索・右ペインキャッシュ用）
            _bgCts.Cancel();
            _bgCts = new CancellationTokenSource();
            _ = BackgroundFetchProgramInfoAsync(_recList, client, _bgCts.Token);

            // ② 全件の基本情報を取得して検索用リストを構築
            _allRecCts.Cancel();
            _allRecCts = new CancellationTokenSource();
            _allRecList  = [];
            _pagedAllRec = [];
            _ = LoadAllRecInfoAsync(client, _allRecCts.Token);
        }
    }

    /// <summary>
    /// 検索用に全件の基本情報を取得する（programInfo は空のまま）。
    /// 完了後、検索ボックスに文字が入っていれば自動的に検索ビューへ切り替える。
    /// </summary>
    private async Task LoadAllRecInfoAsync(EmwuiClient client, CancellationToken ct)
    {
        try
        {
            var (all, _) = await client.GetRecFileInfoAsync(65535, 0);
            if (ct.IsCancellationRequested) return;
            all.Sort((a, b) => b.StartTime.CompareTo(a.StartTime));

            await Dispatcher.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested) return;
                _allRecList = all;

                // 検索中 or ソートが保留中なら全件ページモードへ切り替え
                if (!string.IsNullOrEmpty(RecSearchBox.Text) || _sortModePending)
                {
                    _sortModePending = false;
                    _allRecPagedPage = 0;
                    EnterAllRecPagedMode();
                }
            });

            // _allRecList の全件番組情報を並列でバックグラウンド取得。
            // _recList で先行キャッシュ済みの分は ContainsKey でスキップ。
            if (!ct.IsCancellationRequested)
                _ = BackgroundFetchProgramInfoAsync(all, client, ct);
        }
        catch (OperationCanceledException) { }
        catch { /* 全件取得失敗は無視（ページ検索で代替） */ }
    }


    /// <summary>
    /// 録画一覧の番組情報（programInfo）をバックグラウンドで取得する。
    /// stopOnEmpty=true の場合は新しい順に逐次取得し、番組情報が空のアイテムに当たった時点で中断。
    /// stopOnEmpty=false の場合は 8並列で全件取得。
    /// </summary>
    private async Task BackgroundFetchProgramInfoAsync(
        List<RecFileInfo> items, EmwuiClient client, CancellationToken ct,
        bool stopOnEmpty = false)
    {
        if (stopOnEmpty)
        {
            // 新しい順（items はソート済み）に逐次取得。番組情報なし → 以降中断。
            int seqDone = 0;
            foreach (var item in items)
            {
                if (ct.IsCancellationRequested) break;
                if (_programInfoCache.ContainsKey(item.ID)) continue;
                try
                {
                    var text = await client.GetProgramInfoTextAsync(item.ID, ct);
                    if (string.IsNullOrEmpty(text)) break; // 番組情報なし → 中断
                    item.ProgramInfo = text;
                    _programInfoCache[item.ID] = text;
                    seqDone++;
                    if (seqDone % 10 == 0)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (ct.IsCancellationRequested) return;
                            StatusText.Text = $"番組情報取得中... {seqDone}";
                            if (!string.IsNullOrEmpty(RecSearchBox.Text))
                            {
                                if (_isAllRecPagedMode) EnterAllRecPagedMode();
                                else _recView?.Refresh();
                            }
                        });
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { break; }
            }
            if (!ct.IsCancellationRequested)
                await Dispatcher.InvokeAsync(() =>
                {
                    StatusText.Text = "全文検索可";
                    if (!string.IsNullOrEmpty(RecSearchBox.Text) && _isAllRecPagedMode)
                        EnterAllRecPagedMode();
                    else
                        _recView?.Refresh();
                });
            return;
        }

        const int concurrency = 8;
        var sem = new SemaphoreSlim(concurrency);
        int done = 0;
        int total = items.Count;

        var tasks = items.Select(async item =>
        {
            await sem.WaitAsync(ct);
            try
            {
                // ContainsKey で確認: 既キャッシュ済みの ID は再フェッチしない
                if (!_programInfoCache.ContainsKey(item.ID) && !ct.IsCancellationRequested)
                {
                    var text = await client.GetProgramInfoTextAsync(item.ID, ct);
                    item.ProgramInfo = text;
                    if (!string.IsNullOrEmpty(text))
                        _programInfoCache[item.ID] = text;
                }

                int n = Interlocked.Increment(ref done);
                if (n % 10 == 0 || n == total)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (ct.IsCancellationRequested) return;
                        StatusText.Text = n < total
                            ? $"番組情報取得中... {n}/{total}"
                            : "全文検索可";
                        // 検索中なら随時フィルター再評価
                        if (!string.IsNullOrEmpty(RecSearchBox.Text))
                        {
                            if (_isAllRecPagedMode)
                                EnterAllRecPagedMode();
                            else
                                _recView?.Refresh();
                        }
                    });
                }
            }
            catch (OperationCanceledException) { }
            finally { sem.Release(); }
        });

        await Task.WhenAll(tasks);

        if (!ct.IsCancellationRequested)
            await Dispatcher.InvokeAsync(() =>
            {
                if (!string.IsNullOrEmpty(RecSearchBox.Text) && _isAllRecPagedMode)
                    EnterAllRecPagedMode();
                else
                    _recView?.Refresh();
            });
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
            || data.StationName.Contains(q, StringComparison.OrdinalIgnoreCase);
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
        if (e.Key == Key.Enter) ApplySearchMode();
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
        _resView?.Refresh();
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

        if (_allRecList.Count > 0)
        {
            // 全件取得済み → フィルタ+ソートして全件ページモードへ
            _sortModePending = false;
            _allRecPagedPage = 0;
            EnterAllRecPagedMode();
        }
        else
        {
            // 全件未取得 → ページビューのみ並べ替え、取得後に全件ページモードへ切り替え
            _recView?.SortDescriptions.Clear();
            if (_recSortProp != null)
                _recView?.SortDescriptions.Add(new SortDescription(_recSortProp,
                    _recSortAsc ? ListSortDirection.Ascending : ListSortDirection.Descending));
            _sortModePending = true;
            StatusText.Text = "全件データ読み込み後に全件ソートを適用します...";
        }
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
                  _resOrigHeaders, _resView);
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

    private async void RecInfoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 前の番組情報取得をキャンセル
        _epgCts.Cancel();
        _epgCts = new CancellationTokenSource();
        var ct = _epgCts.Token;

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

        // ── 番組情報: キャッシュ済みなら即表示、なければ EnumRecInfo?id=N を取得 ──
        // _programInfoCache を先に確認（バックグラウンドフェッチ済みかもしれない）
        if (_programInfoCache.TryGetValue(info.ID, out var cached) && !string.IsNullOrEmpty(cached))
        {
            ShowProgramInfo(cached);
            return;
        }
        if (!string.IsNullOrEmpty(info.ProgramInfo))
        {
            // info オブジェクト自体にセット済み（2回目クリック等）→ 即時表示
            ShowProgramInfo(info.ProgramInfo);
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.EmwuiBaseUrl)) return;

        // "取得中..." を出しておいてから非同期フェッチ
        RecProgramInfoLabel.Visibility = Visibility.Visible;
        RecProgramInfo.Visibility      = Visibility.Visible;
        RecProgramInfo.Text            = "番組情報取得中...";

        var progText = await new EmwuiClient(_settings.EmwuiBaseUrl)
            .GetProgramInfoTextAsync(info.ID, ct);

        if (ct.IsCancellationRequested) return;   // 別の行が選ばれた

        // RecFileInfo に書き戻してキャッシュ（次回クリック時は HTTP 不要）
        // _programInfoCache にも登録して全文検索でも使えるようにする
        info.ProgramInfo = progText;
        if (!string.IsNullOrEmpty(progText))
            _programInfoCache[info.ID] = progText;
        ShowProgramInfo(progText);
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
        _epgCts.Cancel();
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

    private void ReserveList_SelectionChanged(object sender, SelectionChangedEventArgs e)
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
    }

    private void ClearResPanel()
    {
        ResTitle.Text = "";
        ResStation.Text = "";
        ResDateTime.Text = "";
        ResStatus.Text = "";
        ResComment.Text = "";
    }

    private void MainTab_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

    /// <summary>
    /// PreviewKeyDown（トンネリング）で ListView より先にページ移動キーを処理する。
    /// ListView は PageDown/Home/End をスクロール・選択移動として消費するため、
    /// KeyDown（バブリング）まで届かない。PreviewKeyDown なら確実に先取りできる。
    /// </summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 編集可能 TextBox 入力中はカーソル移動を優先
        if (Keyboard.FocusedElement is TextBox tb && !tb.IsReadOnly) return;

        switch (e.Key)
        {
            case Key.PageDown:
                NextPage_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.PageUp:
                PrevPage_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.Home:
                FirstPage_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.End:
                LastPage_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// KeyDown（バブリング）。F5・Enter・↑↓を処理する。
    /// ↑↓は ListView にフォーカスがある場合 ListView 側で処理済み（Handled）のため
    /// ここには届かない。右ペイン等にフォーカスがある場合のみ MoveRecListSelection を呼ぶ。
    /// </summary>
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        var isEditable = Keyboard.FocusedElement is TextBox tb && !tb.IsReadOnly;

        switch (e.Key)
        {
            case Key.F5:
                Reload();
                e.Handled = true;
                break;
            case Key.Enter when !isEditable:
                RecSearch_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.Up when !isEditable:
                MoveRecListSelection(-1);
                e.Handled = true;
                break;
            case Key.Down when !isEditable:
                MoveRecListSelection(+1);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// 録画一覧の選択を delta 行分移動してスクロールする。
    /// ListView 自身にフォーカスがない状態（右ペイン等を見ている間）でも
    /// ↑↓キーで選択を動かすために Window_KeyDown から呼ぶ。
    /// </summary>
    private void MoveRecListSelection(int delta)
    {
        var count = RecInfoList.Items.Count;
        if (count == 0) return;
        var next = Math.Clamp(RecInfoList.SelectedIndex + delta, 0, count - 1);
        RecInfoList.SelectedIndex = next;
        RecInfoList.ScrollIntoView(RecInfoList.SelectedItem);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow(_settings);
        if (win.ShowDialog() == true)
        {
            _settings = win.Settings;
            _settings.Save();
            _playServer?.UpdateSettings(_settings);
            // 設定保存だけではリロードしない。F5 または「更新」で手動取得。
            StatusText.Text = "設定を保存しました。F5 または「更新」で再読み込みできます。";
        }
    }

    // ── 再生サーバー ──────────────────────────────────────────────────────────

    private void StartPlayServer()
    {
        try
        {
            _playServer = new PlayServer(_settings.PlayServerPort, _settings);
            _playServer.Start();
        }
        catch (Exception ex)
        {
            _playServer = null;
            System.Diagnostics.Debug.WriteLine($"PlayServer 起動失敗: {ex.Message}");
        }
    }
}
