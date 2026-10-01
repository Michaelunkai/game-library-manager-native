using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using static GameLibrary.Native.ThemeTokens;

namespace GameLibrary.Native;

// DESIGN.md is the authoritative description of the palette; native\Theme.xaml is
// the live token source. This type turns both into a single typed, testable truth
// so the design system cannot silently rot: the palette guard (rule 2), the 4px
// spacing scale, and the WCAG contrast table are all enforced from inside the app.
internal static class ThemeTokens
{
    internal const string Background = "#101217";
    internal const string Surface = "#171B23";
    internal const string Panel = "#1D222C";
    internal const string Stroke = "#303847";
    internal const string Text = "#F2F5FA";
    internal const string Muted = "#A9B5C9";
    internal const string Accent = "#9CE7BB";
    internal const string AccentInk = "#0E1A12";
    internal const string Danger = "#7A2B2B";
    internal const string Stop = "#A83232";
    internal const string NeutralInk = "#FFFFFF";

    private static readonly string[] TokenNames =
    {
        "background", "surface", "panel", "stroke", "text", "muted",
        "accent", "accentInk", "danger", "stop", "neutralInk"
    };

    // Every hex permitted in markup: the palette constants plus the two named
    // destructive fills. Nothing else may appear literally in MainWindow.xaml.
    internal static IReadOnlyList<string> AllowedHexValues() =>
        new[] { Background, Surface, Panel, Stroke, Text, Muted, Accent, AccentInk, Danger, Stop, NeutralInk }
            .Select(NormalizeHex)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    // Enforces DESIGN.md rule 2. Returns every #RRGGBB / #AARRGGBB not in the
    // allowed set, with the 1-based source line. Hex inside an XML comment is
    // ignored; comparison is case-insensitive.
    internal static IReadOnlyList<string> FindIllegalHexInMarkup(string xamlText)
    {
        if (xamlText is null) throw new ArgumentNullException(nameof(xamlText));
        string masked = MaskComments(xamlText);
        var allowed = new HashSet<string>(AllowedHexValues(), StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (Match match in HexRegex.Matches(masked))
        {
            if (allowed.Contains(match.Value)) continue;
            result.Add($"line {LineOf(masked, match.Index)}: {match.Value}");
        }
        return result;
    }

    // Enforces DESIGN.md's 4px spacing scale. Returns every Margin/Padding literal
    // that is not a multiple of 4, excluding legitimate 0 values. Handles both the
    // single-value and a,b,c,d forms.
    internal static IReadOnlyList<string> FindOffScaleSpacing(string xamlText)
    {
        if (xamlText is null) throw new ArgumentNullException(nameof(xamlText));
        string masked = MaskComments(xamlText);
        var result = new List<string>();
        foreach (Match match in SpacingRegex.Matches(masked))
        {
            string attribute = match.Groups[1].Value;
            string value = match.Groups[2].Value;
            var offScale = new List<string>();
            foreach (string raw in value.Split(','))
            {
                string token = raw.Trim();
                if (token.Length == 0) continue;
                if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) continue;
                if (number == 0.0) continue;
                if (number % 4.0 != 0.0) offScale.Add(token);
            }
            if (offScale.Count > 0)
                result.Add($"line {LineOf(masked, match.Index)}: {attribute}=\"{value}\" (off-scale: {string.Join(", ", offScale)})");
        }
        return result;
    }

    internal static string Describe() =>
        $"Graphite palette: {TokenNames.Length} tokens ({string.Join(", ", TokenNames)}).";

    // WCAG 2.1 relative-luminance contrast: linearise sRGB, then (L1+0.05)/(L2+0.05).
    internal static double ContrastRatio(string hexA, string hexB)
    {
        double luminanceA = RelativeLuminance(hexA);
        double luminanceB = RelativeLuminance(hexB);
        double lighter = Math.Max(luminanceA, luminanceB);
        double darker = Math.Min(luminanceA, luminanceB);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(string hex)
    {
        (int r, int g, int b) = ParseHex(hex);
        return 0.2126 * Linearize(r / 255.0) + 0.7152 * Linearize(g / 255.0) + 0.0722 * Linearize(b / 255.0);
    }

    private static double Linearize(double channel) =>
        channel <= 0.03928 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);

    private static (int R, int G, int B) ParseHex(string hex)
    {
        if (hex is null) throw new ArgumentNullException(nameof(hex));
        string value = hex.Trim();
        if (value.StartsWith("#", StringComparison.Ordinal)) value = value.Substring(1);
        if (value.Length != 6) throw new ArgumentException($"Expected a #RRGGBB value, got '{hex}'.", nameof(hex));
        int r = int.Parse(value.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        int g = int.Parse(value.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        int b = int.Parse(value.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return (r, g, b);
    }

    private static string NormalizeHex(string hex)
    {
        (int r, int g, int b) = ParseHex(hex);
        return "#" + r.ToString("X2", CultureInfo.InvariantCulture)
                   + g.ToString("X2", CultureInfo.InvariantCulture)
                   + b.ToString("X2", CultureInfo.InvariantCulture);
    }

    // Replaces comment bodies with blanks so their line structure survives: a hex
    // or margin inside <!-- --> is ignored, but following lines keep their numbers.
    private static string MaskComments(string text) =>
        Regex.Replace(text, "<!--.*?-->", match =>
        {
            var builder = new StringBuilder(match.Value.Length);
            foreach (char c in match.Value) builder.Append(c == '\n' ? '\n' : ' ');
            return builder.ToString();
        }, RegexOptions.Singleline);

    private static int LineOf(string text, int index)
    {
        int line = 1;
        int limit = Math.Min(index, text.Length);
        for (int i = 0; i < limit; i++) if (text[i] == '\n') line++;
        return line;
    }

    private static readonly Regex HexRegex = new("#[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?", RegexOptions.Compiled);
    private static readonly Regex SpacingRegex = new("\\b(Margin|Padding)\\s*=\\s*\"([^\"]*)\"", RegexOptions.Compiled);
}

internal static class ThemeTokensTests
{
    internal static void Run(string root)
    {
        int failures = 0;
        void Check(string name, Action test)
        {
            try { test(); Console.WriteLine("  PASS  " + name); }
            catch (Exception ex) { failures++; Console.WriteLine("  FAIL  " + name + " :: " + ex.Message); }
        }
        void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        void Near(double a, double b, double epsilon, string message)
        {
            if (Math.Abs(a - b) > epsilon) throw new InvalidOperationException(message + $" ({a.ToString("R", CultureInfo.InvariantCulture)} vs {b.ToString("R", CultureInfo.InvariantCulture)})");
        }

        Console.WriteLine("ThemeTokensTests");

        Check("palette constants match the live Theme.xaml brushes", () =>
        {
            string themePath = LocateThemeXaml(root);
            string themeText = File.ReadAllText(themePath);
            var brushes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in Regex.Matches(themeText, "<SolidColorBrush\\s+x:Key=\"([^\"]+)\"\\s+Color=\"(#[0-9A-Fa-f]{6})\""))
                brushes[match.Groups[1].Value] = match.Groups[2].Value.ToUpperInvariant();

            void Same(string key, string expected)
            {
                Require(brushes.TryGetValue(key, out string? actual), $"Theme.xaml has no SolidColorBrush '{key}'.");
                Require(string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase), $"Theme.xaml {key} is {actual}, token expects {expected}.");
            }
            Same("CanvasBrush", Background);
            Same("PanelBrush", Surface);
            Same("CardBrush", Panel);
            Same("StrokeBrush", Stroke);
            Same("TextBrush", Text);
            Same("MutedBrush", Muted);
            Same("AccentBrush", Accent);
            Require(AccentInk == "#0E1A12", "DESIGN.md accentInk is authoritative and must be #0E1A12.");
            Console.WriteLine("    theme: " + themePath);
        });

        Check("ContrastRatio is symmetric and bounded", () =>
        {
            Near(ContrastRatio("#000000", "#FFFFFF"), 21.0, 0.001, "black against white must be 21");
            Near(ContrastRatio("#FFFFFF", "#000000"), 21.0, 0.001, "white against black must be 21");
            Near(ContrastRatio(Accent, Background), ContrastRatio(Background, Accent), 1e-9, "ratio must be symmetric");
            Near(ContrastRatio(Accent, Accent), 1.0, 1e-9, "a colour against itself must be 1");
        });

        Check("DESIGN.md contrast table still passes every threshold", () =>
        {
            var pairs = new (string Name, string Fg, string Bg, double Need)[]
            {
                ("foreground on bg", Text, Background, 4.5),
                ("foreground on surface", Text, Surface, 4.5),
                ("foreground on panel", Text, Panel, 4.5),
                ("secondary on bg", Muted, Background, 4.5),
                ("secondary on surface", Muted, Surface, 4.5),
                ("accent on bg", Accent, Background, 3.0),
                ("accent on surface", Accent, Surface, 3.0),
                ("accent on panel", Accent, Panel, 3.0),
                ("accentInk on accent", AccentInk, Accent, 4.5),
                ("dangerInk on danger", NeutralInk, Danger, 4.5),
                ("stopInk on stop", NeutralInk, Stop, 4.5),
                ("stroke on bg", Stroke, Background, 1.5),
            };
            Console.WriteLine("    pair                             ratio  need  result");
            foreach (var pair in pairs)
            {
                double ratio = ContrastRatio(pair.Fg, pair.Bg);
                string verdict = ratio >= pair.Need ? "PASS" : "FAIL";
                Console.WriteLine($"    {pair.Name,-32} {ratio,5:N2}  {pair.Need,4:N1}  {verdict}");
                Require(ratio >= pair.Need - 1e-9, $"{pair.Name} is {ratio:N2}, needs {pair.Need:N1}.");
            }
        });

        Check("FindIllegalHexInMarkup flags off-palette, accepts palette, ignores comments", () =>
        {
            var flagged = FindIllegalHexInMarkup("<Border Background=\"#FF00FF\"/>");
            Require(flagged.Count == 1 && flagged[0].Contains("#FF00FF", StringComparison.OrdinalIgnoreCase), "out-of-palette hex was not flagged");
            var accepted = FindIllegalHexInMarkup("<Border Background=\"#9ce7bb\" BorderBrush=\"#9CE7BB\"/>");
            Require(accepted.Count == 0, "in-palette hex (either case) was flagged");
            var commented = FindIllegalHexInMarkup("<Border Background=\"#9CE7BB\"/><!-- #FF00FF -->");
            Require(commented.Count == 0, "hex inside an XML comment was flagged");
        });

        Check("FindOffScaleSpacing flags 7px, accepts the 4px scale, handles four values", () =>
        {
            var accepted = FindOffScaleSpacing("<A Margin=\"0\"/><B Margin=\"4\"/><C Margin=\"8\"/><D Margin=\"12\"/><E Margin=\"16\"/><F Margin=\"24\"/><G Margin=\"32\"/><H Margin=\"8,12,16,24\"/>");
            Require(accepted.Count == 0, "on-scale spacing was flagged: " + string.Join("; ", accepted));
            var seven = FindOffScaleSpacing("<A Margin=\"7\"/>");
            Require(seven.Count == 1 && seven[0].Contains("7", StringComparison.Ordinal), "7px margin was not flagged");
            var fourValue = FindOffScaleSpacing("<A Padding=\"1,2,3,4\"/>");
            Require(fourValue.Count == 1, "four-value off-scale padding was not flagged");
            var zero = FindOffScaleSpacing("<A Margin=\"0\"/><B Padding=\"0,0,0,0\"/>");
            Require(zero.Count == 0, "legitimate zero spacing was flagged");
        });

        Console.WriteLine($"  {failures} failure(s)");
        if (failures > 0) throw new InvalidOperationException($"{failures} ThemeTokens test(s) failed.");
    }

    private static string LocateThemeXaml(string root)
    {
        var probes = new List<string>();
        void Walk(string? start)
        {
            string? dir = start;
            while (!string.IsNullOrEmpty(dir))
            {
                probes.Add(Path.Combine(dir, "native", "Theme.xaml"));
                probes.Add(Path.Combine(dir, "Theme.xaml"));
                dir = Path.GetDirectoryName(dir);
            }
        }
        Walk(root);
        Walk(AppContext.BaseDirectory);
        Walk(Environment.CurrentDirectory);
        foreach (string probe in probes.Distinct(StringComparer.OrdinalIgnoreCase))
            if (File.Exists(probe)) return probe;
        throw new FileNotFoundException("Could not locate native\\Theme.xaml from root or the executing assembly.");
    }
}
