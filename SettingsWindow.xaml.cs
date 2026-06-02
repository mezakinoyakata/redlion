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
            EmwuiBaseUrl       = current.EmwuiBaseUrl,
            MaxRecItems        = current.MaxRecItems,
            RecordingFolder    = current.RecordingFolder,
            PlayerPath         = current.PlayerPath,
            RefreshIntervalSeconds = current.RefreshIntervalSeconds,
            PlayServerPort     = current.PlayServerPort,
            EncodedFolder      = current.EncodedFolder,
            DbConnectionString = current.DbConnectionString,
        };
        EmwuiUrlBox.Text           = Settings.EmwuiBaseUrl;
        MaxRecItemsBox.Text        = Settings.MaxRecItems.ToString();
        RecFolderBox.Text          = Settings.RecordingFolder;
        PlayerPathBox.Text         = Settings.PlayerPath;
        EncodedFolderBox.Text      = Settings.EncodedFolder;
        DbConnectionStringBox.Text = Settings.DbConnectionString;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Settings.EmwuiBaseUrl    = EmwuiUrlBox.Text.Trim();
        if (int.TryParse(MaxRecItemsBox.Text.Trim(), out var n) && n > 0)
            Settings.MaxRecItems = n;
        Settings.RecordingFolder     = RecFolderBox.Text.Trim();
        Settings.PlayerPath          = PlayerPathBox.Text.Trim();
        Settings.EncodedFolder       = EncodedFolderBox.Text.Trim();
        Settings.DbConnectionString  = DbConnectionStringBox.Text.Trim();
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

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "エンコード済みフォルダを選択",
            InitialDirectory = EncodedFolderBox.Text,
        };
        if (dlg.ShowDialog() == true)
            EncodedFolderBox.Text = dlg.FolderName;
    }
}
