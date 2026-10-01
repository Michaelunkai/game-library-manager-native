using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;

namespace GameLibrary.Native;

/// <summary>
/// The keyboard focus visual layer, loaded from FocusVisuals.xaml. The manager
/// applies it with one call:
/// <code>Application.Current.Resources.MergedDictionaries.Add(new FocusVisuals());</code>
/// </summary>
internal sealed partial class FocusVisuals : ResourceDictionary
{
    public FocusVisuals()
    {
        InitializeComponent();
    }
}

/// <summary>
/// A pure, testable view of FocusVisuals.xaml. A resource dictionary cannot be
/// unit tested directly, so the rules that matter (no literal hex, only real
/// Theme.xaml keys, one 6px radius, on-scale spacing) are checked against the
/// shipped XAML text.
/// </summary>
internal static class FocusVisualsContract
{
    /// <summary>The Theme.xaml keys this dictionary is allowed to reference.</summary>
    internal static IReadOnlyList<string> RequiredResourceKeys() => new[]
    {
        "CardBrush",
        "StrokeBrush",
        "TextBrush",
        "AccentBrush",
    };

    /// <summary>
    /// False when any hex colour appears in the text at all. The focus layer must
    /// take every colour from a Theme.xaml key, so the shipped file must contain
    /// zero hex literals.
    /// </summary>
    internal static bool UsesOnlyPaletteColours(string xamlText)
    {
        if (xamlText is null) throw new ArgumentNullException(nameof(xamlText));
        return !HexRegex.IsMatch(xamlText);
    }

    /// <summary>
    /// Every Margin/Padding literal that is not a multiple of the 4px scale,
    /// excluding legitimate zero values. Handles the single-value and a,b,c,d forms.
    /// </summary>
    internal static IReadOnlyList<string> FindOffScaleValues(string xamlText)
    {
        if (xamlText is null) throw new ArgumentNullException(nameof(xamlText));
        var result = new List<string>();
        foreach (Match match in SpacingRegex.Matches(xamlText))
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
                result.Add(attribute + "=\"" + value + "\" (off-scale: " + string.Join(", ", offScale) + ")");
        }
        return result;
    }

    /// <summary>The Theme.xaml keys referenced by StaticResource or DynamicResource.</summary>
    internal static IReadOnlyList<string> ReferencedResourceKeys(string xamlText)
    {
        if (xamlText is null) throw new ArgumentNullException(nameof(xamlText));
        var keys = new List<string>();
        foreach (Match match in ResourceRefRegex.Matches(xamlText))
        {
            string key = match.Groups[1].Value;
            if (!keys.Contains(key, StringComparer.Ordinal)) keys.Add(key);
        }
        return keys;
    }

    private static readonly Regex HexRegex = new("#[0-9A-Fa-f]{3,8}\\b", RegexOptions.Compiled);
    private static readonly Regex SpacingRegex = new("\\b(Margin|Padding)\\s*=\\s*\"([^\"]*)\"", RegexOptions.Compiled);
    private static readonly Regex ResourceRefRegex = new("\\{(?:Static|Dynamic)Resource\\s+([A-Za-z0-9_]+)\\}", RegexOptions.Compiled);
}

internal static class FocusVisualsContractTests
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static void Run(string root)
    {
        string visualsPath = Locate(root, "FocusVisuals.xaml");
        string themePath = Locate(root, "Theme.xaml");
        string visuals = File.ReadAllText(visualsPath);
        string visualsMarkup = MaskComments(visuals);
        string theme = File.ReadAllText(themePath);

        // 1. No literal hex anywhere: every colour comes from a Theme.xaml key.
        Require(FocusVisualsContract.UsesOnlyPaletteColours(visuals),
            "FocusVisuals.xaml contains a literal hex colour; the focus layer must use Theme.xaml keys only.");
        int hexCount = Regex.Matches(visuals, "#[0-9A-Fa-f]{3,8}\\b").Count;
        Require(hexCount == 0, "Expected zero hex values in FocusVisuals.xaml, found " + hexCount + ".");

        // 2. Every referenced key exists in Theme.xaml, and the contract list is complete.
        var themeKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(theme, "x:Key=\"([^\"]+)\"")) themeKeys.Add(match.Groups[1].Value);
        var used = FocusVisualsContract.ReferencedResourceKeys(visualsMarkup);
        Require(used.Count > 0, "FocusVisuals.xaml references no Theme.xaml keys.");
        var required = FocusVisualsContract.RequiredResourceKeys();
        foreach (string key in used)
        {
            Require(themeKeys.Contains(key), "FocusVisuals.xaml references '" + key + "', which Theme.xaml does not define.");
            Require(required.Contains(key, StringComparer.Ordinal),
                "FocusVisuals.xaml references '" + key + "', which is missing from RequiredResourceKeys().");
        }
        foreach (string key in required)
        {
            Require(themeKeys.Contains(key), "RequiredResourceKeys lists '" + key + "', which Theme.xaml does not define.");
            Require(used.Contains(key, StringComparer.Ordinal), "RequiredResourceKeys lists '" + key + "' but the XAML never uses it.");
        }

        // 3. Every corner radius is the single 6px token.
        var radii = Regex.Matches(visualsMarkup, "CornerRadius=\"([^\"]+)\"")
            .Cast<Match>().Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
        Require(radii.Length == 1 && radii[0] == "6",
            "Every corner radius must be the 6px token; found: " + string.Join(", ", radii) + ".");

        // 4. No off-scale Margin or Padding.
        var offScale = FocusVisualsContract.FindOffScaleValues(visualsMarkup);
        Require(offScale.Count == 0, "FocusVisuals.xaml has off-scale spacing: " + string.Join("; ", offScale));

        // 5. The ring matches FocusPolicy exactly, and there is no animation.
        string? themeText = ThemeBrushValue(theme, "TextBrush");
        Require(themeText is not null, "Theme.xaml does not define TextBrush.");
        Require(string.Equals(themeText, FocusPolicy.FocusRingColor, StringComparison.OrdinalIgnoreCase),
            "The ring must use FocusPolicy.FocusRingColor (" + FocusPolicy.FocusRingColor + "), but TextBrush is " + themeText + ".");
        Require(visualsMarkup.Contains("IsKeyboardFocused", StringComparison.Ordinal),
            "FocusVisuals.xaml has no IsKeyboardFocused trigger, so the ring would never appear.");
        Require(visualsMarkup.Contains("FocusVisualStyle", StringComparison.Ordinal),
            "FocusVisuals.xaml does not set FocusVisualStyle, so the default dotted rectangle can double up.");
        Require(Regex.IsMatch(visualsMarkup, "BorderThickness=\"2\""),
            "FocusVisuals.xaml does not draw the ring at FocusPolicy.FocusRingThickness (2).");
        Require(!Regex.IsMatch(visualsMarkup,
                "<Storyboard|BeginStoryboard|(?:Double|Color|Thickness|Object|Point)Animation", RegexOptions.IgnoreCase),
            "FocusVisuals.xaml animates; the motion policy forbids animating anything the list recycles.");

        // 6. Both destructive styles and the checkbox style are present.
        Require(visualsMarkup.Contains("DestructiveDeleteButton", StringComparison.Ordinal),
            "The Delete destructive button has no named focus style.");
        Require(visualsMarkup.Contains("DestructiveStopButton", StringComparison.Ordinal),
            "The Exit game plus Wand destructive button has no named focus style.");
        Require(visualsMarkup.Contains("TargetType=\"CheckBox\"", StringComparison.Ordinal),
            "The card checkbox has no focus style.");

        // 7. The contract helpers themselves behave on known inputs.
        Require(!FocusVisualsContract.UsesOnlyPaletteColours("<X Color=\"#FFFFFF\"/>"),
            "UsesOnlyPaletteColours did not flag a literal hex.");
        Require(FocusVisualsContract.UsesOnlyPaletteColours("<X Color=\"{DynamicResource TextBrush}\"/>"),
            "UsesOnlyPaletteColours wrongly flagged a resource-key reference.");
        Require(FocusVisualsContract.FindOffScaleValues("<X Margin=\"7\"/>").Count == 1,
            "FindOffScaleValues did not flag a 7px margin.");
        Require(FocusVisualsContract.FindOffScaleValues("<X Padding=\"16,8\"/>").Count == 0,
            "FindOffScaleValues wrongly flagged on-scale padding.");
        Require(FocusVisualsContract.FindOffScaleValues("<X Margin=\"0\"/><Y Padding=\"0,0,0,0\"/>").Count == 0,
            "FindOffScaleValues wrongly flagged zero spacing.");

        if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
        {
            var report = new List<string>
            {
                "FocusVisuals contract report",
                "xaml: " + visualsPath,
                "hex values: " + hexCount,
                "off-scale spacing: " + offScale.Count,
                "corner radius: " + string.Join(", ", radii),
                "keys: " + string.Join(", ", used),
                "ring: TextBrush " + themeText + " at 2px (FocusPolicy " + FocusPolicy.FocusRingColor + " / " + FocusPolicy.FocusRingThickness.ToString(CultureInfo.InvariantCulture) + ")",
            };
            File.WriteAllLines(Path.Combine(root, "focus-visuals-report.txt"), report);
        }
    }

    private static string MaskComments(string text) =>
        Regex.Replace(text, "<!--.*?-->", match => new string(' ', match.Value.Length), RegexOptions.Singleline);

    private static string? ThemeBrushValue(string themeText, string key)
    {
        Match match = Regex.Match(themeText,
            "<SolidColorBrush\\s+x:Key=\"" + Regex.Escape(key) + "\"\\s+Color=\"(#[0-9A-Fa-f]{6})\"");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string Locate(string? root, string fileName)
    {
        var probes = new List<string>();
        void Walk(string? start)
        {
            string? dir = start;
            while (!string.IsNullOrEmpty(dir))
            {
                probes.Add(Path.Combine(dir, "native", fileName));
                probes.Add(Path.Combine(dir, fileName));
                dir = Path.GetDirectoryName(dir);
            }
        }
        Walk(root);
        Walk(AppContext.BaseDirectory);
        Walk(Environment.CurrentDirectory);
        foreach (string probe in probes.Distinct(StringComparer.OrdinalIgnoreCase))
            if (File.Exists(probe)) return probe;
        throw new FileNotFoundException("Could not locate native\\" + fileName + " from root or the executing assembly.");
    }
}
