using EthLinkTester.App.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;

namespace EthLinkTester.App;

public sealed partial class ShellWindow : Window
{
    // Tag -> page. Keeping this as data rather than a switch means adding a section is a
    // one-line change here plus the XAML item, with no navigation logic to keep in sync.
    private static readonly Dictionary<string, Type> Pages = new()
    {
        ["rig"] = typeof(RigPage),
        ["suite"] = typeof(SuitePage),
        ["lab"] = typeof(LabPage),
        ["library"] = typeof(LibraryPage),
        ["reports"] = typeof(ReportsPage),
        ["poe"] = typeof(PoePage),
    };

    public ShellWindow()
    {
        InitializeComponent();

        // Mica Alt is the layered variant Windows 11 uses for apps with a navigation pane -
        // it tints the pane and content differently, which reads correctly with NavigationView.
        SystemBackdrop = new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt };

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        Title = "eth-link-tester";
        SizeAndCentre();

        NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>().First();
    }

    /// <summary>
    /// Sizes relative to the display's work area rather than to fixed pixel dimensions.
    /// AppWindow works in physical pixels, so a hardcoded size shrinks on a high-DPI display -
    /// on a 2560x1440 panel at 175% scaling, a "1360x900" window renders at an effective
    /// 777x514 and clips the navigation pane. Deriving from the work area is DPI-independent.
    /// </summary>
    private void SizeAndCentre()
    {
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;

        var width = Math.Min(1600, (int)(work.Width * 0.80));
        var height = Math.Min(1040, (int)(work.Height * 0.88));

        AppWindow.MoveAndResize(new RectInt32(
            work.X + ((work.Width - width) / 2),
            work.Y + ((work.Height - height) / 2),
            width,
            height));
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            Navigate(typeof(SettingsPage));
            return;
        }

        if (args.SelectedItem is NavigationViewItem { Tag: string tag } && Pages.TryGetValue(tag, out var page))
        {
            Navigate(page);
        }
    }

    private void Navigate(Type page)
    {
        if (ContentFrame.CurrentSourcePageType == page)
        {
            return;
        }

        ContentFrame.Navigate(page, null, new EntranceNavigationTransitionInfo());
    }
}
