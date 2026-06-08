using System.Collections.ObjectModel;
using System.Windows;

namespace EDCBViewer;

public partial class SettingsWindow : Window
{
    public AppSettings Settings { get; private set; }
    private readonly ObservableCollection<string> _folderItems;

    public SettingsWindow(AppSettings current)
    {
        InitializeComponent();
        Settings = new AppSettings
        {
            MaxRecItems        = current.MaxRecItems,
            PlayerPath         = current.PlayerPath,
            EncodedFolder      = current.EncodedFolder,
            DbConnectionString = current.DbConnectionString,
        };
        _folderItems = new ObservableCollection<string>(current.EncodedFolders);
        MaxRecItemsBox.Text        = Settings.MaxRecItems.ToString();
        PlayerPathBox.Text         = Settings.PlayerPath;
        DbConnectionStringBox.Text = Settings.DbConnectionString;
        FolderListBox.ItemsSource  = _folderItems;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(MaxRecItemsBox.Text.Trim(), out var n) && n > 0)
            Settings.MaxRecItems = n;
        Settings.PlayerPath         = PlayerPathBox.Text.Trim();
        Settings.DbConnectionString = DbConnectionStringBox.Text.Trim();
        Settings.EncodedFolders     = _folderItems.ToList();
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
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "プレイヤーを選択",
            Filter = "実行ファイル (*.exe)|*.exe|すべてのファイル (*.*)|*.*",
            FileName = PlayerPathBox.Text
        };
        if (dlg.ShowDialog() == true)
            PlayerPathBox.Text = dlg.FileName;
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "フォルダを追加",
            InitialDirectory = _folderItems.LastOrDefault() ?? "",
        };
        if (dlg.ShowDialog() != true) return;
        var path = dlg.FolderName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!_folderItems.Contains(path, StringComparer.OrdinalIgnoreCase))
            _folderItems.Add(path);
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (FolderListBox.SelectedItem is string path)
            _folderItems.Remove(path);
    }
}
