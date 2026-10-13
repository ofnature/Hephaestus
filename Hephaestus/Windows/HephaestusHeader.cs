using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Hephaestus.Windows.Components;

namespace Hephaestus.Windows;

/// <summary>
/// Identity strip across the top of the main window: the flame mark, the name, "Forged in Fire",
/// a status pill, a detail line, and the settings shortcut — ember-lit, in the forge idiom.
/// </summary>
internal static class HephaestusHeader
{
    public static void Draw(string status, Vector4 statusAccent, string detail, Action openSettings)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var size = new Vector2(ImGui.GetContentRegionAvail().X, HephaestusTheme.HeaderHeight * scale);
        var origin = ImGui.GetCursorScreenPos();
        var end = origin + size;
        var dl = ImGui.GetWindowDrawList();

        var left = Vector4.Lerp(HephaestusTheme.CardBg, HephaestusTheme.AccentGold, 0.16f);
        var right = Vector4.Lerp(HephaestusTheme.CardBg, HephaestusTheme.AccentSoft, 0.06f);
        dl.AddRectFilledMultiColor(origin, end,
            ImGui.GetColorU32(left), ImGui.GetColorU32(right), ImGui.GetColorU32(right), ImGui.GetColorU32(left));
        dl.AddRect(origin, end, ImGui.GetColorU32(HephaestusTheme.WithAlpha(HephaestusTheme.AccentSoft, 0.38f)),
            HephaestusTheme.CardRounding * scale, ImDrawFlags.None, 1.2f * scale);

        DrawFlame(origin + new Vector2(40f, 42f) * scale, scale, dl);
        DrawEmbers(origin, size, scale, dl);

        var textX = origin.X + 66f * scale;
        ImGui.SetWindowFontScale(1.62f);
        dl.AddText(new Vector2(textX, origin.Y + 16f * scale), ImGui.GetColorU32(HephaestusTheme.TextPrimary), "HEPHAESTUS");
        ImGui.SetWindowFontScale(0.78f);
        dl.AddText(new Vector2(textX, origin.Y + 50f * scale), ImGui.GetColorU32(HephaestusTheme.AccentSoft), "FORGED IN FIRE");
        ImGui.SetWindowFontScale(1f);

        var pillSize = Pill.Measure(status);
        Pill.DrawAt(new Vector2(end.X - 18f * scale - pillSize.X, origin.Y + 16f * scale), status, statusAccent);

        ImGui.SetWindowFontScale(0.82f);
        var detailSize = ImGui.CalcTextSize(detail);
        dl.AddText(new Vector2(end.X - 18f * scale - detailSize.X, origin.Y + 46f * scale), ImGui.GetColorU32(HephaestusTheme.TextDim), detail);
        ImGui.SetWindowFontScale(1f);

        var button = 24f * scale;
        ImGui.SetCursorScreenPos(new Vector2(end.X - 18f * scale - button, end.Y - 30f * scale));
        if (Buttons.Icon(FontAwesomeIcon.Cog, "##hephaestus_settings", button, "Settings"))
            openSettings();

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(size);
    }

    /// <summary>The mark: a flickering two-tone flame with a soft glow.</summary>
    private static void DrawFlame(Vector2 center, float scale, ImDrawListPtr dl)
    {
        var flicker = 1f + 0.06f * HephaestusTheme.Pulse(700.0);
        var flameH = 19f * scale * flicker;
        var flameW = 10f * scale;
        var tip = new Vector2(center.X, center.Y - flameH);
        var baseL = new Vector2(center.X - flameW, center.Y + 6f * scale);
        var baseR = new Vector2(center.X + flameW, center.Y + 6f * scale);
        var gold = ImGui.GetColorU32(HephaestusTheme.AccentGold);
        var soft = ImGui.GetColorU32(HephaestusTheme.AccentSoft);

        dl.AddCircleFilled(center - new Vector2(0f, flameH * 0.3f), 15f * scale,
            ImGui.GetColorU32(HephaestusTheme.WithAlpha(HephaestusTheme.AccentGold, 0.14f)));

        dl.AddTriangleFilled(tip, baseL, baseR, gold);
        var coreH = flameH * 0.55f;
        dl.AddTriangleFilled(
            new Vector2(center.X, center.Y - coreH),
            new Vector2(center.X - flameW * 0.45f, center.Y + 2f * scale),
            new Vector2(center.X + flameW * 0.45f, center.Y + 2f * scale),
            soft);
    }

    /// <summary>Seven embers drifting up through the banner.</summary>
    private static void DrawEmbers(Vector2 origin, Vector2 size, float scale, ImDrawListPtr dl)
    {
        const int count = 7;
        var ember = new Vector4(0.95f, 0.76f, 0.31f, 1f);
        for (var i = 0; i < count; i++)
        {
            var p = HephaestusTheme.Phase(5200.0 + i * 730.0);
            var x = origin.X + 40f * scale + (i + 0.5f) * ((size.X - 80f * scale) / count);
            var y = origin.Y + size.Y - p * size.Y;
            var alpha = MathF.Sin(p * MathF.PI) * 0.7f;
            if (alpha <= 0.02f)
                continue;
            dl.AddCircleFilled(new Vector2(x, y), (1.4f + i % 3 * 0.5f) * scale,
                ImGui.GetColorU32(HephaestusTheme.WithAlpha(ember, alpha)));
        }
    }

    /// <summary>
    /// Compact single-line header for the floating crafting windows (crafting-log dock, assist
    /// overlay): flame mark + wordmark + a right-aligned section label. The full 88px banner is
    /// only for the main window.
    /// </summary>
    public static void DrawCompact(string sectionLabel)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var height = 34f * scale;
        var size = new Vector2(ImGui.GetContentRegionAvail().X, height);
        var origin = ImGui.GetCursorScreenPos();
        var end = origin + size;
        var dl = ImGui.GetWindowDrawList();

        var left = Vector4.Lerp(HephaestusTheme.CardBg, HephaestusTheme.AccentGold, 0.20f);
        var right = Vector4.Lerp(HephaestusTheme.CardBg, HephaestusTheme.AccentSoft, 0.08f);
        dl.AddRectFilledMultiColor(origin, end,
            ImGui.GetColorU32(left), ImGui.GetColorU32(right), ImGui.GetColorU32(right), ImGui.GetColorU32(left));
        dl.AddRect(origin, end, ImGui.GetColorU32(HephaestusTheme.WithAlpha(HephaestusTheme.AccentSoft, 0.32f)),
            HephaestusTheme.CardRounding * scale, ImDrawFlags.None, 1.0f * scale);

        var cx = origin.X + 20f * scale;
        var cy = origin.Y + 17f * scale;
        var fh = 11f * scale;
        var fw = 6f * scale;
        dl.AddTriangleFilled(new Vector2(cx, cy - fh), new Vector2(cx - fw, cy + 3f * scale), new Vector2(cx + fw, cy + 3f * scale),
            ImGui.GetColorU32(HephaestusTheme.AccentGold));
        dl.AddTriangleFilled(new Vector2(cx, cy - fh * 0.55f), new Vector2(cx - fw * 0.45f, cy + 1f * scale), new Vector2(cx + fw * 0.45f, cy + 1f * scale),
            ImGui.GetColorU32(HephaestusTheme.AccentSoft));

        var textX = origin.X + 34f * scale;
        dl.AddText(new Vector2(textX, origin.Y + 8f * scale), ImGui.GetColorU32(HephaestusTheme.TextPrimary), "HEPHAESTUS");

        var label = sectionLabel.ToUpperInvariant();
        ImGui.SetWindowFontScale(0.72f);
        var labelSize = ImGui.CalcTextSize(label);
        dl.AddText(new Vector2(end.X - 12f * scale - labelSize.X, origin.Y + 10f * scale), ImGui.GetColorU32(HephaestusTheme.AccentSoft), label);
        ImGui.SetWindowFontScale(1f);

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(size);
    }
}
