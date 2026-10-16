using System.Drawing;
using System.Text.RegularExpressions;
using Glimmer.WinForms;
using Xunit;

namespace BoardScout.Tests;

// The window's version badge (GDI+) and the browser dashboard's (glimmer-badge.css) are the same orb:
// aurora's colours in every state must match the CSS that ships in Shell/glimmer.
public class VersionBadgeTests
{
    private static readonly string Css = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shell", "glimmer", "glimmer-badge.css"));

    [Theory]
    [InlineData(GlimmerState.Idle, "[data-preset=\"aurora\"] {")]
    [InlineData(GlimmerState.Success, "[data-preset=\"aurora\"][data-state=\"success\"]")]
    [InlineData(GlimmerState.Error, "[data-preset=\"aurora\"][data-state=\"error\"]")]
    public void Native_aurora_matches_the_web_badge(GlimmerState state, string selector)
    {
        var start = Css.IndexOf(selector, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{selector} is in glimmer-badge.css");
        var block = Css[start..Css.IndexOf('}', start)];
        string Web(string name) => Regex.Match(block, $@"--gd-{name}: #([0-9A-Fa-f]{{6}})").Groups[1].Value.ToUpperInvariant();
        static string Hex(Color c) => $"{c.R:X2}{c.G:X2}{c.B:X2}";

        var native = GlimmerBadgePresets.Get("aurora").For(state);
        Assert.Equal(Web("a"), Hex(native.A));
        Assert.Equal(Web("b"), Hex(native.B));
        Assert.Equal(Web("c"), Hex(native.C));
        Assert.Equal(Web("d"), Hex(native.D));
        Assert.Equal(Web("hi"), Hex(native.Hi));
        Assert.Equal(Web("glow"), Hex(native.Glow));
        Assert.Equal(Web("canvas"), Hex(native.Canvas));
        Assert.Equal(Web("rim-a"), Hex(native.RimA));
        Assert.Equal(Web("rim-b"), Hex(native.RimB));
    }

    [Fact]
    public void Unknown_presets_fall_back_to_siri_as_on_the_web()
    {
        Assert.Contains("aurora", GlimmerBadgePresets.Names);
        Assert.Same(GlimmerBadgePresets.Get("siri"), GlimmerBadgePresets.Get("no-such-preset"));
    }
}
