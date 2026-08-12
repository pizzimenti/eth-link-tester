using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EthLinkTester.App.Controls;

/// <summary>
/// Shared layout for a navigation section that is not built yet. Being explicit about which
/// phase a section arrives in is more useful than an empty page, and it doubles as a
/// project-status view while the app is under construction.
/// </summary>
public sealed partial class PhaseStub : UserControl
{
    public static readonly DependencyProperty HeadingProperty =
        DependencyProperty.Register(nameof(Heading), typeof(string), typeof(PhaseStub), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty PhaseLabelProperty =
        DependencyProperty.Register(nameof(PhaseLabel), typeof(string), typeof(PhaseStub), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SummaryProperty =
        DependencyProperty.Register(nameof(Summary), typeof(string), typeof(PhaseStub), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DetailsProperty =
        DependencyProperty.Register(nameof(Details), typeof(object), typeof(PhaseStub), new PropertyMetadata(null));

    public PhaseStub() => InitializeComponent();

    public string Heading
    {
        get => (string)GetValue(HeadingProperty);
        set => SetValue(HeadingProperty, value);
    }

    public string PhaseLabel
    {
        get => (string)GetValue(PhaseLabelProperty);
        set => SetValue(PhaseLabelProperty, value);
    }

    public string Summary
    {
        get => (string)GetValue(SummaryProperty);
        set => SetValue(SummaryProperty, value);
    }

    public object? Details
    {
        get => GetValue(DetailsProperty);
        set => SetValue(DetailsProperty, value);
    }
}
