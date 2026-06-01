using System.Windows;
using System.Windows.Threading;

namespace EDCBViewer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show(ex.Exception.ToString(), "クラッシュ情報",
                MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            var msg = (ex.ExceptionObject as Exception)?.ToString() ?? ex.ExceptionObject.ToString();
            MessageBox.Show(msg, "クラッシュ情報（非UIスレッド）",
                MessageBoxButton.OK, MessageBoxImage.Error);
        };
    }
}

