using EthLinkTester.App.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace EthLinkTester.App.Views;

public sealed partial class RigPage : Page
{
    private bool _initialized;

    public RigPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
    }

    internal RigViewModel ViewModel { get; } = new();

    /// <summary>
    /// Probes on first navigation only.
    /// </summary>
    /// <remarks>
    /// The page is cached, so this fires again on every return to it. Re-probing each time would
    /// be merely wasteful, but re-running recovery would not: it reports what a previous run left
    /// behind, and repeating that message after the user has already read and dismissed it would
    /// claim a second failure that never happened. Re-probing on demand is what the button is for.
    /// </remarks>
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await ViewModel.InitializeAsync();
    }
}
