namespace CodexHistory.App;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            if (e.Args.Length == 2 && e.Args[0].Equals("--smoke", StringComparison.OrdinalIgnoreCase))
            {
                var window = new MainWindow();
                MainWindow = window;
                window.Show();
                await window.RunSmokeAsync(e.Args[1]);
                window.Close();
                Shutdown(0);
                return;
            }

            if (e.Args.Length > 0)
            {
                System.Windows.MessageBox.Show(
                    "Kullanım: CodexHistory.App.exe [--smoke <çıktı.png>]",
                    "Codex Geçmişi",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
            }

            var splash = new SplashWindow();
            splash.Show();
            await System.Windows.Threading.Dispatcher.Yield(
                System.Windows.Threading.DispatcherPriority.Render);

            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            mainWindow.Show();
            splash.Close();
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                $"Uygulama başlatılamadı: {exception.Message}",
                "Codex Geçmişi",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
