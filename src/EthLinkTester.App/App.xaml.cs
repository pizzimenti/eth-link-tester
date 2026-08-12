using Microsoft.UI.Xaml;

namespace EthLinkTester.App;

public partial class App : Application
{
    internal static Window? MainWindow { get; private set; }

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new ShellWindow();
        MainWindow.Activate();
    }
}
