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
            EmwuiBaseUrl           = current.EmwuiBaseUrl,
            RecordingFolder        = current.RecordingFolder,
            PlayerPath             = current.PlayerPath,
            RefreshIntervalSeconds = current.RefreshIntervalSeconds,
            PlayServerPort         = current.PlayServerPort,
        };
        EmwuiUrlBox.Text      = Settings.EmwuiBaseUrl;
        MaxRecItemsBox.Text   = Settings.MaxRecItems.ToString();
        RecFolderBox.Text     = Settings.RecordingFolder;
        PlayerPathBox.Text    = Settings.PlayerPath;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Settings.EmwuiBaseUrl    = EmwuiUrlBox.Text.Trim();
        if (int.TryParse(MaxRecItemsBox.Text.Trim(), out var n) && n > 0)
            Settings.MaxRecItems = n;
        Settings.RecordingFolder = RecFolderBox.Text.Trim();
        Settings.PlayerPath      = PlayerPathBox.Text.Trim();
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
