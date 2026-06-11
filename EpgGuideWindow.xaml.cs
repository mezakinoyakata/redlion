using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using EDCBViewer.Models;
using EDCBViewer.Services;

namespace EDCBViewer;

/// <summary>
/// MySQL の events テーブルから取得した番組表を表示する。
/// EpgTimerSrv の過去番組表 (EnumPgArc) と違い、events に残っている限り
/// 過去のどの日付でも参照できる。
/// </summary>
public partial class EpgGuideWindow : Window
{
    private readonly EpgDbReader _reader;
    private DateTime _day;          // 表示日（その日の 04:00 起点で24時間）
    private bool _loading;
    private Border? _selectedBlock;

    private const double PxPerMin   = 3.0;
    private const double ColWidth   = 170.0;
    private const int    DayStartHour = 4;   // 04:00〜翌04:00

    // nibble_l1 (ジャンル大分類) → ダークテーマ向け背景色
    private static readonly Dictionary<int, Brush> GenreBrushes = new()
    {
        [0]  = MakeBrush("#FF3E4A56"), // ニュース/報道
        [1]  = MakeBrush("#FF2E4A5E"), // スポーツ
        [2]  = MakeBrush("#FF4A3E2E"), // 情報/ワイドショー
        [3]  = MakeBrush("#FF562E3E"), // ドラマ
        [4]  = MakeBrush("#FF56402E"), // 音楽
        [5]  = MakeBrush("#FF4A4A2E"), // バラエティ
        [6]  = MakeBrush("#FF3E2E56"), // 映画
        [7]  = MakeBrush("#FF2E5630"), // アニメ/特撮
        [8]  = MakeBrush("#FF2E5656"), // ドキュメンタリー/教養
        [9]  = MakeBrush("#FF502E50"), // 劇場/公演
        [10] = MakeBrush("#FF36502E"), // 趣味/教育
        [11] = MakeBrush("#FF40404A"), // 福祉
    };
    private static readonly Brush DefaultBlockBrush = MakeBrush("#FF333333");
    private static readonly Brush SelectedBorderBrush = MakeBrush("#FF0078D4");
    private static readonly Brush BlockBorderBrush = MakeBrush("#FF1E1E1E");

    private static SolidColorBrush MakeBrush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public EpgGuideWindow(string dbConnectionString)
    {
        InitializeComponent();
        _reader = new EpgDbReader(dbConnectionString);
        _day = TodayBase();
        DateBox.SelectedDate = _day;
        Loaded += (_, _) => LoadGuide();
    }

    /// <summary>「今日」の番組表基準日（午前4時前なら前日扱い）。</summary>
    private static DateTime TodayBase()
    {
        var now = DateTime.Now;
        return now.Hour < DayStartHour ? now.Date.AddDays(-1) : now.Date;
    }

    private DateTime RangeStart => _day.AddHours(DayStartHour);
    private DateTime RangeEnd   => RangeStart.AddHours(24);

    // ─── ナビゲーション ──────────────────────────────────────────────────

    private void PrevDay_Click(object sender, RoutedEventArgs e) => SetDay(_day.AddDays(-1));
    private void NextDay_Click(object sender, RoutedEventArgs e) => SetDay(_day.AddDays(1));
    private void Today_Click(object sender, RoutedEventArgs e)   => SetDay(TodayBase());

    private void DateBox_SelectedDateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DateBox.SelectedDate is DateTime d && d.Date != _day)
            SetDay(d.Date);
    }

    private void SetDay(DateTime day)
    {
        _day = day;
        DateBox.SelectedDate = day;
        LoadGuide();
    }

    private void MainScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        HeaderScroll.ScrollToHorizontalOffset(e.HorizontalOffset);
        TimeScroll.ScrollToVerticalOffset(e.VerticalOffset);
    }

    // ─── 描画 ────────────────────────────────────────────────────────────

    private async void LoadGuide()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            DateLabel.Text  = $"{_day:yyyy/MM/dd (ddd)}";
            StatusText.Text = "読み込み中...";

            var lo = RangeStart;
            var hi = RangeEnd;
            var events = await Task.Run(() => _reader.GetGuideEvents(lo, hi));

            BuildGrid(events);
            StatusText.Text = events.Count == 0
                ? "この日の番組データはありません"
                : $"{events.Count} 番組";
        }
        finally { _loading = false; }
    }

    private void BuildGrid(List<EpgEvent> events)
    {
        GuideCanvas.Children.Clear();
        TimeCanvas.Children.Clear();
        HeaderPanel.Children.Clear();
        _selectedBlock = null;
        ClearDetail();

        // サービス列: クエリの ORDER BY（地デジ→BS→CS、リモコンキー順）を保持
        var cols = new Dictionary<ulong, int>();
        var colNames = new List<string>();
        foreach (var ev in events)
        {
            var key = ((ulong)ev.ONID << 32) | ((ulong)ev.TSID << 16) | ev.SID;
            if (!cols.ContainsKey(key))
            {
                cols[key] = cols.Count;
                colNames.Add(ev.ServiceName);
            }
        }

        double gridHeight = 24 * 60 * PxPerMin;
        GuideCanvas.Width  = Math.Max(1, cols.Count) * ColWidth;
        GuideCanvas.Height = gridHeight;
        TimeCanvas.Height  = gridHeight;

        // 時刻軸 (04 .. 27時)
        for (int h = 0; h < 24; h++)
        {
            double y = h * 60 * PxPerMin;
            var lbl = new TextBlock
            {
                Text = $"{(DayStartHour + h) % 24}",
                Foreground = Brushes.Gray,
                FontSize = 14,
                Width = 40,
                TextAlignment = TextAlignment.Center,
            };
            Canvas.SetTop(lbl, y + 2);
            Canvas.SetLeft(lbl, 0);
            TimeCanvas.Children.Add(lbl);

            var line = new Rectangle
            {
                Width = GuideCanvas.Width, Height = 1,
                Fill = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            };
            Canvas.SetTop(line, y);
            Canvas.SetLeft(line, 0);
            GuideCanvas.Children.Add(line);
        }

        // サービス名ヘッダー
        foreach (var name in colNames)
        {
            HeaderPanel.Children.Add(new Border
            {
                Width = ColWidth,
                Background = MakeBrush("#FF2D2D2D"),
                BorderBrush = MakeBrush("#FF444444"),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Child = new TextBlock
                {
                    Text = name,
                    Foreground = Brushes.White,
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });
        }

        // 列区切り線
        for (int c = 1; c < cols.Count; c++)
        {
            var vline = new Rectangle
            {
                Width = 1, Height = gridHeight,
                Fill = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            };
            Canvas.SetLeft(vline, c * ColWidth);
            Canvas.SetTop(vline, 0);
            GuideCanvas.Children.Add(vline);
        }

        // 番組ブロック
        foreach (var ev in events)
        {
            if (ev.StartTime is not DateTime st) continue;
            var key = ((ulong)ev.ONID << 32) | ((ulong)ev.TSID << 16) | ev.SID;
            int col = cols[key];

            double top = (st - RangeStart).TotalMinutes * PxPerMin;
            double durMin = ev.DurationSec.HasValue ? ev.DurationSec.Value / 60.0 : 10;
            double height = Math.Max(durMin * PxPerMin, 14);
            if (top + height > gridHeight) height = gridHeight - top;
            if (height <= 0) continue;

            var brush = ev.ContentNibble.HasValue &&
                        GenreBrushes.TryGetValue(ev.ContentNibble.Value, out var gb)
                        ? gb : DefaultBlockBrush;

            var text = new TextBlock
            {
                Foreground = MakeBrush("#FFE0E0E0"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            text.Inlines.Add(new System.Windows.Documents.Run($"{st:HH:mm} ")
            {
                Foreground = Brushes.Gray, FontSize = 10,
            });
            text.Inlines.Add(ev.EventName);

            var block = new Border
            {
                Width = ColWidth - 2,
                Height = Math.Max(height - 1, 2),
                Background = brush,
                BorderBrush = BlockBorderBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(3, 1, 3, 1),
                Child = text,
                Tag = ev,
                ToolTip = $"{st:HH:mm} {ev.EventName}",
                ClipToBounds = true,
            };
            block.MouseLeftButtonDown += Block_Click;
            Canvas.SetLeft(block, col * ColWidth + 1);
            Canvas.SetTop(block, top);
            GuideCanvas.Children.Add(block);
        }

        // 現在時刻ライン（表示日が今日のときのみ）
        var now = DateTime.Now;
        if (now >= RangeStart && now < RangeEnd)
        {
            var nowLine = new Rectangle
            {
                Width = GuideCanvas.Width, Height = 2,
                Fill = MakeBrush("#FFD04040"),
                IsHitTestVisible = false,
            };
            double y = (now - RangeStart).TotalMinutes * PxPerMin;
            Canvas.SetTop(nowLine, y);
            Canvas.SetLeft(nowLine, 0);
            GuideCanvas.Children.Add(nowLine);
            MainScroll.ScrollToVerticalOffset(Math.Max(0, y - 200));
        }
        else
        {
            MainScroll.ScrollToVerticalOffset(0);
        }
    }

    // ─── 詳細表示 ────────────────────────────────────────────────────────

    private async void Block_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border block || block.Tag is not EpgEvent ev) return;

        if (_selectedBlock != null)
        {
            _selectedBlock.BorderBrush = BlockBorderBrush;
            _selectedBlock.BorderThickness = new Thickness(1);
        }
        block.BorderBrush = SelectedBorderBrush;
        block.BorderThickness = new Thickness(2);
        _selectedBlock = block;

        DetailTitle.Text   = ev.EventName;
        DetailService.Text = ev.ServiceName;
        DetailTime.Text    = ev.StartTime is DateTime st
            ? ev.DurationSec.HasValue
                ? $"{st:yyyy/MM/dd HH:mm} - {st.AddSeconds(ev.DurationSec.Value):HH:mm}"
                : $"{st:yyyy/MM/dd HH:mm}"
            : "";
        DetailInfoLabel.Visibility = Visibility.Collapsed;
        DetailInfo.Visibility = Visibility.Collapsed;

        // 本文は events から個別取得（番組表クエリには含めていない）
        var info = await Task.Run(() =>
            _reader.GetEventInfoText(ev.ONID, ev.TSID, ev.SID, ev.EventID));
        // 取得中に別ブロックが選択された場合は破棄
        if (_selectedBlock != block) return;
        if (!string.IsNullOrEmpty(info))
        {
            DetailInfo.Text = info;
            DetailInfoLabel.Visibility = Visibility.Visible;
            DetailInfo.Visibility = Visibility.Visible;
        }
    }

    private void ClearDetail()
    {
        DetailTitle.Text = "";
        DetailService.Text = "";
        DetailTime.Text = "";
        DetailInfo.Text = "";
        DetailInfoLabel.Visibility = Visibility.Collapsed;
        DetailInfo.Visibility = Visibility.Collapsed;
    }
}
