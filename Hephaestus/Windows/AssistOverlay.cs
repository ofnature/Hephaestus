using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Hephaestus.Features.Crafting.Driving;
using Hephaestus.Features.Crafting.Simulation;
using Hephaestus.Services.Game;
using Hephaestus.Windows.Components;

namespace Hephaestus.Windows;

/// <summary>
/// Beside the Synthesis window while a craft is in progress: the action to press NOW, large, then
/// the next few planned steps dimmed — they are tentative by nature, since every condition roll can
/// change them — and one line saying whether the plan is being followed or why it is not. Shown in
/// both modes: in Assisted it is the instruction, in Auto it is the explanation.
/// </summary>
public sealed class AssistOverlay : Window
{
    private static readonly Vector2 NextIconSize = new(48f, 48f);
    private static readonly Vector2 PreviewIconSize = new(28f, 28f);
    private static readonly Vector4 Dimmed = new(1f, 1f, 1f, 0.45f);

    private readonly CraftSession _session;
    private readonly SynthesisReader _reader;
    private readonly GameActionMap _actions;
    private readonly ITextureProvider _textures;
    private readonly HotbarReader _hotbars;
    private readonly Func<bool> _highlight;

    /// <param name="highlight">Whether to outline the action on the hotbars right now (Assisted mode, and switched on).</param>
    public AssistOverlay(
        CraftSession session, SynthesisReader reader, GameActionMap actions, ITextureProvider textures,
        HotbarReader hotbars, Func<bool> highlight)
        : base("Hephaestus — Next action##HephaestusAssist",
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoFocusOnAppearing)
    {
        _session = session;
        _reader = reader;
        _actions = actions;
        _textures = textures;
        _hotbars = hotbars;
        _highlight = highlight;

        ShowCloseButton = false;
        RespectCloseHotkey = false;
        DisableWindowSounds = true;
        IsOpen = false;
    }

    public override void PreDraw()
    {
        // Appearing only: it opens docked to the Synthesis window, then stays where you drag it.
        if (_reader.WindowTopRight is { } anchor)
            ImGuiHelpers.SetNextWindowPosRelativeMainViewport(anchor + new Vector2(4f, 0f), ImGuiCond.Appearing);
    }

    /// <summary>"about 45 s left" / "about 1 min 20 s left" - rounded to five seconds: it is an estimate and should read like one.</summary>
    internal static string TimeLeft(double seconds)
    {
        var rounded = Math.Max(5, (int)Math.Round(seconds / 5.0) * 5);
        return rounded < 60 ? $"about {rounded} s left" : $"about {rounded / 60} min {rounded % 60:00} s left";
    }

    public override void Draw()
    {
        HephaestusHeader.DrawCompact("Next action");

        if (_session.Current is not { Action: { } next } recommendation)
        {
            ImGui.TextColored(HephaestusTheme.TextSecondary, _session.Status);
            return;
        }

        DrawIcon(next, NextIconSize, Vector4.One);
        ImGui.SameLine();
        ImGui.BeginGroup();
        ImGui.TextColored(HephaestusTheme.AccentGold, Name(next));
        Pill.Draw(
            recommendation.FollowsBaseline ? "on plan" : "off plan",
            recommendation.FollowsBaseline ? HephaestusTheme.StatusGreen : HephaestusTheme.StatusYellow);
        if (recommendation.SecondsLeft >= 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(HephaestusTheme.TextSecondary, TimeLeft(recommendation.SecondsLeft));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("An estimate for this item: the solver's own expected steps, at about 2.5 s for an action that works the item and 1.25 s for the rest.");
        }

        ImGui.EndGroup();

        if (recommendation.Preview.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(HephaestusTheme.TextSecondary, "then, if conditions stay Normal:");
            for (var i = 0; i < recommendation.Preview.Count; i++)
            {
                if (i > 0)
                    ImGui.SameLine();
                DrawIcon(recommendation.Preview[i], PreviewIconSize, Dimmed);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(Name(recommendation.Preview[i]));
            }
        }

        ImGui.Spacing();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 300f);
        ImGui.TextColored(HephaestusTheme.TextSecondary, _session.Status);
        if (_highlight())
            HighlightOnHotbars(next);
        ImGui.PopTextWrapPos();
    }

    /// <summary>
    /// Outline every visible hotbar slot holding the action, on the foreground draw list (so it is
    /// not clipped to this window). When there is nothing to outline, say which kind of nothing:
    /// the action simply is not slotted, or the two hotbar sources disagreed.
    /// </summary>
    private void HighlightOnHotbars(CraftAction action)
    {
        var slots = _hotbars.Find(action);
        if (slots.Count == 0)
        {
            ImGui.TextColored(HephaestusTheme.StatusGrey, _hotbars.LastCandidates == 0
                ? "Not on a visible hotbar."
                : "On a hotbar, but the bar itself disagrees — not highlighted. Please report this.");
            return;
        }

        // A slow pulse: easy to find at a glance, without strobing beside a fast animation.
        var pulse = 0.65f + (0.35f * MathF.Sin((float)ImGui.GetTime() * 5f));
        var gold = HephaestusTheme.AccentGold;
        var line = ImGui.ColorConvertFloat4ToU32(new Vector4(gold.X, gold.Y, gold.Z, pulse));
        var glow = ImGui.ColorConvertFloat4ToU32(new Vector4(gold.X, gold.Y, gold.Z, pulse * 0.35f));

        // Node positions are relative to the game window; ImGui's are absolute once viewports are on.
        var origin = ImGuiHelpers.MainViewport.Pos;
        var canvas = ImGui.GetForegroundDrawList(ImGuiHelpers.MainViewport);
        foreach (var slot in slots)
        {
            var min = origin + slot.Position;
            var max = min + slot.Size;
            var rounding = slot.Size.X * 0.15f;
            canvas.AddRect(min - new Vector2(3f), max + new Vector2(3f), glow, rounding, ImDrawFlags.RoundCornersAll, 6f);
            canvas.AddRect(min, max, line, rounding, ImDrawFlags.RoundCornersAll, 3f);
        }
    }

    private void DrawIcon(CraftAction action, Vector2 size, Vector4 tint)
    {
        var icon = _actions.IconFor(action);
        if (icon == 0)
        {
            ImGui.Dummy(size);
            return;
        }

        var texture = _textures.GetFromGameIcon(new GameIconLookup(icon)).GetWrapOrEmpty();
        ImGui.Image(texture.Handle, size, Vector2.Zero, Vector2.One, tint);
    }

    /// <summary>"PreparatoryTouch" -> "Preparatory Touch". English, like the rest of the UI.</summary>
    private static string Name(CraftAction action)
    {
        var raw = action.ToString();
        var spaced = new System.Text.StringBuilder(raw.Length + 4);
        for (var i = 0; i < raw.Length; i++)
        {
            if (i > 0 && (char.IsUpper(raw[i]) || (char.IsDigit(raw[i]) && !char.IsDigit(raw[i - 1]))))
                spaced.Append(' ');
            spaced.Append(raw[i]);
        }

        return spaced.ToString();
    }
}
