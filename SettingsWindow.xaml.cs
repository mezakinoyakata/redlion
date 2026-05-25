using System.Windows;
using Microsoft.Win32;

namespace EDCBViewer;

public partial class SettingsWindow : Window
{
    public AppSettings Settings { get; private set; }

    public SettingsWindow(AppSettings current)
    {
        InitializeComponent();
        Settings = new AppSettings
        {
            RecInfoPath = current.RecInfoPath,
            ReservePath = current.ReservePath,
            RecordingFolder = current.RecordingFolder,
            PlayerPath = current.PlayerPath,
            RefreshIntervalSeconds = current.RefreshIntervalSeconds
        };
        RecInfoPathBox.Text = Settings.RecInfoPath;
        ReservePathBox.Text = Settings.ReservePath;
        RecFolderBox.Text = Settings.RecordingFolder;
        PlayerPathBox.Text = Settings.PlayerPath;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Settings.RecInfoPath = RecInfoPathBox.Text.Trim();
        Settings.ReservePath = ReservePathBox.Text.Trim();
        Settings.RecordingFolder = RecFolderBox.Text.Trim();
        Settings.PlayerPath = PlayerPathBox.Text.Trim();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "MPC-BE を選択",
            Filter = "実行ファイル (*.exe)|*.exe|すべてのファイル (*.*)|*.*",
            FileName = PlayerPathBox.Text
        };
        if (dlg.ShowDialog() == true)
            PlayerPathBox.Text = dlg.FileName;
    }
}
