namespace EthLinkTester.App.Controls;

/// <summary>
/// Chart colours, as a single source of truth for every plot in the app.
/// </summary>
/// <remarks>
/// <para>
/// Both schemes are *selected*, not derived by flipping the other. The dark series are the same
/// two hues re-stepped for the dark surface so they keep their contrast and their separation
/// under colour-vision deficiency; inverting the light values would not.
/// </para>
/// <para>
/// Validated with the palette checker on 2026-08-12. Slots 1 and 2 pass every gate in both
/// modes: worst adjacent CVD separation ΔE 24.7 light / 26.8 dark against a target of 8, and
/// both clear 3:1 against their surface.
/// </para>
/// <para>
/// The chart paints its own surface rather than letting the window's Mica backdrop show
/// through. Contrast is only meaningful against a known surface, and a translucent, tinted,
/// wallpaper-dependent background is not one.
/// </para>
/// </remarks>
internal static class VizPalette
{
    internal sealed record Scheme(
        string Surface,
        string Gridline,
        string Axis,
        string Ink,
        string Series1,
        string Series2);

    public static Scheme Light { get; } = new(
        Surface: "#fcfcfb",
        Gridline: "#e1e0d9",
        Axis: "#898781",
        Ink: "#0b0b0b",
        Series1: "#2a78d6",
        Series2: "#eb6834");

    public static Scheme Dark { get; } = new(
        Surface: "#1a1a19",
        Gridline: "#2c2c2a",
        Axis: "#898781",
        Ink: "#ffffff",
        Series1: "#3987e5",
        Series2: "#d95926");

    public static Scheme For(bool isDark) => isDark ? Dark : Light;
}
