using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using EDCBViewer.Models;
using EDCBViewer.Parsers;

namespace EDCBViewer;

public partial class MainWindow : Window
{
    private AppSettings _settings = AppSettings.Load();
    private DispatcherTimer _timer = new();
    private RecFileInfo? _selectedRec;
    private List<RecFileInfo> _recList = [];

    public MainWindow()
    {
        InitializeComponent();
        _timer.Interval = TimeSpan.FromSeconds(_settings.RefreshIntervalSeconds);
        _timer.Tick += (_, _) => Reload();
        _timer.Start();
        Loaded += (_, _) => Reload();
    }

    private async void Reload()
    {
        var recInfoPath = _settings.RecInfoPath;
        var reservePath = _settings.ReservePath;

        // ファイルI/OをバックグラウンドスレッドでUIスレッドから切り離す
        // → ハンドル保持中にUIスレッドがブロックされない
        // → EpgTimerSrvの書き込みタイミングと重なっても影響を最小化
        var (recList, recErr) = await Task.Run(() =>
        {
            try { return (RecInfoParser.Load(recInfoPath), (string?)null); }
            catch (Exception ex) { return ([], ex.Message); }
        });

        var (resList, resErr) = await Task.Run(() =>
        {
            try { return (ReserveParser.Load(reservePath), (string?)null); }
            catch (Exception ex) { return ([], ex.Message); }
        });

        // UI更新はUIスレッドで（awaitで自動的に戻る）
        _recList = recList;
        RecInfoList.ItemsSource = _recList;
        ReserveList.ItemsSource = resList;

        if (recErr != null)
            StatusText.Text = $"RecInfo.txt エラー: {recErr}";
        else if (resErr != null)
            StatusText.Text = $"Reserve.txt エラー: {resErr}";
        else
            StatusText.Text = $"録画済み: {_recList.Count} 件";

        LastUpdateText.Text = $"最終更新: {DateTime.Now:HH:mm:ss}";
    }

    private void RecInfoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecInfoList.SelectedItem is not RecFileInfo info)
        {
            ClearRecPanel();
            return;
        }
        _selectedRec = info;
        RecTitle.Text = info.Title;
        RecService.Text = info.ServiceName;
        RecDateTime.Text = $"{info.StartTime:yyyy/MM/dd HH:mm} ({info.DurationText})";
        RecFilePath.Text = _settings.ToUncPath(info.RecFilePath);
        RecDrops.Text = $"ドロップ: {info.Drops}  スクランブル: {info.Scrambles}";
        RecProgramInfo.Text = string.IsNullOrWhiteSpace(info.ProgramInfo)
            ? info.Comment
            : info.ProgramInfo;
        PlayButton.Visibility = Visibility.Visible;
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

    private void ReserveList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ReserveList.SelectedItem is not ReserveData reserve)
            return;

        // 1. RecInfo一覧からタイトル＋開始時刻が一致するエントリを探す
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

        // 2. 録画中の場合は録画フォルダから追っかけ再生用ファイルを探す
        if (reserve.IsRecording)
        {
            var liveFile = FindRecordingFile(reserve);
            if (liveFile != null)
            {
                // 書き込み中ファイルはFile.Existsが不確かなためチェックをスキップして直接渡す
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

    // 録画フォルダから書き込み中のtsファイルを探す
    // 予約開始時刻の前後5分以内に作成されたファイルを対象にする
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

        // 通常ファイルは存在確認、書き込み中(SMB)はスキップ
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

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow(_settings);
        if (win.ShowDialog() == true)
        {
            _settings = win.Settings;
            _settings.Save();
            _timer.Interval = TimeSpan.FromSeconds(_settings.RefreshIntervalSeconds);
            Reload();
        }
    }
}
