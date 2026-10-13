using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace Hephaestus.Windows;

/// <summary>
/// Hephaestus palette + shared helpers — the Greek-forge dark/gold identity (fire), plus the Argus
/// surface ramp and layout constants so each ported component changes one token. Gold is the
/// identity accent ("the forge"); amber = flagged/waiting, mint = good, rose = a problem.
/// </summary>
internal static class HephaestusTheme
{
    // ---- layout constants (from Argus's Layout.cs) ----
    public const float SidebarWidth = 160f;
    public const float HeaderHeight = 88f;
    public const float StatTileHeight = 58f;
    public const float ActionButtonHeight = 28f;

    // ---- identity: gold / fire ----
    public static readonly Vector4 AccentGold = new(0.85f, 0.65f, 0.20f, 1.00f);
    public static readonly Vector4 AccentSoft = new(0.92f, 0.80f, 0.45f, 1.00f);

    // ---- status accents ----
    public static readonly Vector4 StatusGreen = new(0.20f, 0.75f, 0.35f, 1.00f);
    public static readonly Vector4 StatusYellow = new(0.85f, 0.75f, 0.10f, 1.00f);
    public static readonly Vector4 StatusGrey = new(0.45f, 0.45f, 0.50f, 1.00f);
    public static readonly Vector4 AccentAmber = new(0.92f, 0.74f, 0.34f, 1.00f);
    public static readonly Vector4 AccentMint = new(0.46f, 0.86f, 0.66f, 1.00f);
    public static readonly Vector4 AccentRose = new(0.93f, 0.42f, 0.50f, 1.00f);

    // ---- surfaces ----
    public static readonly Vector4 CardBg = new(0.070f, 0.090f, 0.105f, 0.85f);
    public static readonly Vector4 CardBgSoft = new(0.085f, 0.105f, 0.120f, 0.55f);
    public static readonly Vector4 CardBgHover = new(0.100f, 0.125f, 0.145f, 0.95f);
    public static readonly Vector4 BorderDim = new(0.22f, 0.26f, 0.30f, 1.00f);
    public static readonly Vector4 Hairline = new(1f, 1f, 1f, 0.055f);

    // ---- text ----
    public static readonly Vector4 TextPrimary = new(0.95f, 0.93f, 0.89f, 1.00f);
    public static readonly Vector4 TextSecondary = new(0.78f, 0.80f, 0.84f, 1.00f);
    public static readonly Vector4 TextDim = new(0.55f, 0.58f, 0.62f, 1.00f);
    public static readonly Vector4 TextMuted = new(0.40f, 0.42f, 0.46f, 1.00f);

    public const float CardRounding = 7f;
    public const float FrameRounding = 5f;
    public const float WindowRounding = 7f;

    public const double PulseMedium = 800.0;
    public const double PulseBreath = 2600.0;
    public const double PulseOrbit = 3400.0;

    /// <summary>0..1 sine pulse on the given period, for "something is happening" glows.</summary>
    public static float Pulse(double periodMs = PulseMedium)
    {
        var t = (Environment.TickCount % periodMs) / periodMs;
        return (float)((Math.Sin(t * Math.PI * 2.0) + 1.0) * 0.5);
    }

    public static Vector4 PulseColor(Vector4 a, Vector4 b, double periodMs = PulseMedium)
        => Vector4.Lerp(a, b, Pulse(periodMs));

    public static float Phase(double periodMs)
        => (float)((Environment.TickCount % periodMs) / periodMs);

    public static Vector4 WithAlpha(Vector4 c, float a) => c with { W = a };

    public static void VSpace(float pixels)
        => ImGui.Dummy(new Vector2(0, pixels * ImGuiHelpers.GlobalScale));

    public static void SectionLabel(string label)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, TextDim))
            ImGui.TextUnformatted(label.ToUpperInvariant());
    }

    public static void Text(string text, Vector4 color)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, color))
            ImGui.TextUnformatted(text);
    }

    /// <summary>Text that wraps at the content edge, for sentences rather than labels.</summary>
    public static void TextWrapped(string text, Vector4 color)
    {
        ImGui.PushTextWrapPos(0f);
        Text(text, color);
        ImGui.PopTextWrapPos();
    }

    public static void TextScaled(string text, Vector4 color, float fontScale)
    {
        ImGui.SetWindowFontScale(fontScale);
        Text(text, color);
        ImGui.SetWindowFontScale(1f);
    }

    public static void TextCentered(string text, Vector4 color, float fontScale = 1f)
    {
        if (fontScale != 1f) ImGui.SetWindowFontScale(fontScale);
        var w = ImGui.CalcTextSize(text).X;
        var avail = ImGui.GetContentRegionAvail().X;
        if (avail > w) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (avail - w) * 0.5f);
        Text(text, color);
        if (fontScale != 1f) ImGui.SetWindowFontScale(1f);
    }

    public static IDisposable PushWindowStyle()
    {
        var p = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, FrameRounding);
        p.Push(ImGuiStyleVar.WindowRounding, WindowRounding);
        p.Push(ImGuiStyleVar.ChildRounding, CardRounding);
        p.Push(ImGuiStyleVar.ItemSpacing, new Vector2(8, 7) * ImGuiHelpers.GlobalScale);
        return p;
    }

    public static IDisposable PushCardStyle()
    {
        var p = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, CardRounding * ImGuiHelpers.GlobalScale);
        p.Push(ImGuiStyleVar.ChildBorderSize, 1f);
        p.Push(ImGuiStyleVar.WindowPadding, new Vector2(11, 9) * ImGuiHelpers.GlobalScale);
        p.Push(ImGuiStyleVar.FrameRounding, FrameRounding);
        return p;
    }

    public static void Tooltip(string text, float wrapWidth = 300f)
    {
        if (!ImGui.IsItemHovered())
            return;

        using var tip = ImRaii.Tooltip();
        ImGui.PushTextWrapPos(wrapWidth * ImGuiHelpers.GlobalScale);
        Text(text, TextSecondary);
        ImGui.PopTextWrapPos();
    }

    /// <summary>
    /// Gold-accented section header. This Dalamud ImGui binding has no SeparatorText, so it is
    /// hand-drawn: gold label with a hairline continuing to the right edge (same as Daedalus).
    /// </summary>
    public static void SectionHeader(string label)
    {
        ImGui.Spacing();
        ImGui.TextColored(AccentGold, label);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var lineY = (min.Y + max.Y) / 2f;
        var lineStart = new Vector2(max.X + 8f, lineY);
        var lineEnd = new Vector2(
            ImGui.GetWindowPos().X + ImGui.GetWindowWidth() - ImGui.GetStyle().WindowPadding.X, lineY);
        if (lineEnd.X > lineStart.X)
            ImGui.GetWindowDrawList().AddLine(lineStart, lineEnd,
                ImGui.ColorConvertFloat4ToU32(new Vector4(0.20f, 0.20f, 0.24f, 1f)), 1f);
        ImGui.Spacing();
    }

    /// <summary>Hover "(?)" tooltip for a non-obvious control.</summary>
    public static void HelpMarker(string text)
    {
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }
}
