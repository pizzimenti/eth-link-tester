using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EthLinkTester.App.Controls;

/// <summary>
/// A single headline reading: label, value, unit.
/// </summary>
/// <remarks>
/// Some numbers do not want to be a chart. Current throughput and error count are read as
/// "what is it right now", which a stat tile answers directly and a plot only answers after
/// the reader locates the right end of a line.
/// </remarks>
public sealed partial class StatTile : UserControl
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(StatTile), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(string), typeof(StatTile), new PropertyMetadata("-"));

    public static readonly DependencyProperty UnitProperty =
        DependencyProperty.Register(nameof(Unit), typeof(string), typeof(StatTile), new PropertyMetadata(string.Empty));

    public StatTile() => InitializeComponent();

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public string Unit
    {
        get => (string)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }
}
