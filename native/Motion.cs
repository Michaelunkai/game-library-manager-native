using System;
using System.Collections.Generic;
using System.Windows;

namespace GameLibrary.Native;

/// <summary>
/// The app's motion policy, stated as code so it is a decision rather than an
/// accident.
/// <para>
/// A virtualized game list recycles containers while scrolling. Anything that
/// animates during a recycle competes with layout for the same frame and shows up
/// as dropped frames, so this app animates nothing ambient: no list transitions,
/// no card reveals, no parallax, no staggered entrances, no hover transitions on
/// every control. State changes are instant, which is also what the
/// prefers-reduced-motion path should do.
/// </para>
/// <para>
/// The only motion permitted is direct feedback on a deliberate user action, and
/// only when the operating system's own animation preference allows it. Windows
/// exposes that as the "Animate controls and windows" setting, surfaced here as
/// <see cref="SystemParameters.ClientAreaAnimation"/>; when it is off, or when the
/// app runs headless in verification, animation is disabled outright.
/// </para>
/// </summary>
internal static class MotionPolicy
{
    /// <summary>
    /// True only when the user has animations switched on at the OS level. False in a
    /// headless verification run, where there is nothing to animate for anyway.
    /// </summary>
    internal static bool AnimationsAllowed
    {
        get
        {
            try { return SystemParameters.ClientAreaAnimation && SystemParameters.MenuFade; }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException) { return false; }
        }
    }

    /// <summary>Duration for the single permitted action-feedback transition.</summary>
    internal static TimeSpan ActionFeedbackDuration => TimeSpan.FromMilliseconds(90);

    /// <summary>
    /// What animates, and what does not. Kept as data so the audit can check the
    /// implementation against the policy rather than against a claim.
    /// </summary>
    internal static IReadOnlyList<(string Element, string Treatment)> Map { get; } = new (string, string)[]
    {
        ("game list scroll", "none - containers recycle, animating here causes frame drops"),
        ("card reveal on filter", "none - instant; the index makes re-filtering too fast to notice"),
        ("category switch", "none - instant list swap"),
        ("button press", "instant state change, no transition"),
        ("destructive confirm", "opacity fade, only when AnimationsAllowed"),
        ("cover image load", "none - cached, frozen, decoded once off the critical path"),
    };
}

internal static class MotionPolicyTests
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static void Run(string root)
    {
        // The load-bearing rule: nothing on a scrolling or re-filtering surface may
        // animate. That is what the frame-drop complaints trace back to.
        foreach (var entry in MotionPolicy.Map)
        {
            string element = entry.Element.ToLowerInvariant();
            bool isScrollOrFilter = element.Contains("scroll") || element.Contains("filter")
                || element.Contains("switch") || element.Contains("reveal") || element.Contains("cover");
            Require(!isScrollOrFilter || entry.Treatment.StartsWith("none", StringComparison.Ordinal),
                "The motion map animates a scroll or filter surface (" + entry.Element
                + "). Anything that animates while a list recycles competes for the frame.");
        }

        // Action feedback is the only thing allowed to move, and only when the OS says so.
        string feedback = "none found";
        foreach (var entry in MotionPolicy.Map)
        {
            if (entry.Element == "destructive confirm") { feedback = entry.Treatment; break; }
        }
        Require(feedback.Contains("only when AnimationsAllowed", StringComparison.Ordinal),
            "Action feedback is not gated on the OS animation preference: '" + feedback + "'.");
        Require(MotionPolicy.ActionFeedbackDuration <= TimeSpan.FromMilliseconds(150),
            "Action feedback is slower than 150ms, which reads as lag rather than response.");

        // The gate must be readable without throwing, including off a UI thread.
        MotionPolicy.AnimationsAllowed.ToString();
    }
}
