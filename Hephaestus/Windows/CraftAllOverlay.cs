using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Hephaestus.Features.Crafting;
using Hephaestus.Services.Game;

namespace Hephaestus.Windows;

/// <summary>
/// The count box and Craft All / Craft X button, riding INSIDE the game's crafting log directly
/// above its Synthesize button — where Artisan puts its own. A frameless ImGui window repositioned
/// onto that button every frame (the same technique as Charon's saddlebag overlay), rather than
/// injected native nodes: no extra dependency, and nothing of ours lives in the game's UI tree to
/// leak or crash when the log closes. Opened and closed by the plugin from the log's visibility.
/// </summary>
public sealed class CraftAllOverlay : Window
{
    private const float QueueWidth = 72f;
    private const float CountWidth = 96f;
    private const float Gap = 4f;

    // Game-style gold button, so the overlay reads as part of the log's own "Synthesize" button
    // instead of a floating gray ImGui widget. Tune these to match the game exactly.
    private static readonly Vector4 GoldFill = new(0.71f, 0.56f, 0.29f, 1f);
    private static readonly Vector4 GoldHover = new(0.80f, 0.65f, 0.37f, 1f);
    private static readonly Vector4 GoldActive = new(0.56f, 0.42f, 0.21f, 1f);
    private static readonly Vector4 GoldBorder = new(0.24f, 0.16f, 0.05f, 1f);
    private static readonly Vector4 GoldText = new(0.11f, 0.07f, 0.02f, 1f);
    private static readonly Vector4 GoldHighlight = new(1f, 0.95f, 0.70f, 0.50f);
    private static readonly Vector4 GoldDisabled = new(0.32f, 0.30f, 0.24f, 1f);

    private readonly CraftingLogGame _game;
    private readonly CraftRun _run;
    private readonly CraftRunLauncher _launcher;
    private readonly Action<uint, int> _addToQueue;
    private readonly Func<CraftRunObservation> _observation;

    private Vector2 _buttonSize = new(140f, 28f);
    private float _scale = 1f;
    private bool _stylePushed;

    public CraftAllOverlay(
        CraftingLogGame game, CraftRun run, CraftRunLauncher launcher, Action<uint, int> addToQueue, Func<CraftRunObservation> observation)
        : base("##HephaestusCraftAllOverlay")
    {
        _game = game;
        _run = run;
        _launcher = launcher;
        _addToQueue = addToQueue;
        _observation = observation;

        Flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground
                | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav
                | ImGuiWindowFlags.NoMove;
        RespectCloseHotkey = false;
        DisableWindowSounds = true;
        IsOpen = false;
    }

    public override void PreDraw()
    {
        if (_game.SynthesizeButtonRect is { } rect)
        {
            _buttonSize = rect.Size;
            _scale = rect.Scale;

            // One row directly above Synthesize: Craft All sits over it at the same width, the count
            // box to its left (over Quick Synthesis), and "+ Queue" to the left of that.
            var rowHeight = rect.Size.Y;
            Position = new Vector2(
                rect.Position.X - ((QueueWidth + CountWidth) * _scale) - (2f * Gap * _scale),
                rect.Position.Y - rowHeight - (Gap * _scale));
        }

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(Gap * _scale, 0f));
        _stylePushed = true;
    }

    public override void PostDraw()
    {
        if (!_stylePushed)
            return;

        ImGui.PopStyleVar(2);
        _stylePushed = false;
    }

    public override void Draw()
    {
        var observation = _observation();

        if (_run.Active)
        {
            // Only visible between crafts (the log hides during a synthesis) — long enough to stop.
            if (GoldButton($"Stop ({_run.Completed} / {_run.Requested})", FullRowSize(), _scale, true, primary: false))
                _run.Halt("stopped by you");
            return;
        }

        if (_run.CanResume)
        {
            if (GoldButton($"Resume ({_run.Requested - _run.Completed} left)", FullRowSize(), _scale, true, primary: false))
                _launcher.Resume();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(_run.Status + "\nClear it from the Hephaestus crafting window to start a different run.");
            return;
        }

        if (GoldButton("+ Queue", new Vector2(QueueWidth * _scale, _buttonSize.Y), _scale, observation.SelectedRecipeId != 0))
            _addToQueue(observation.SelectedRecipeId, CraftingLogWindow.QueueQuantity(_launcher.Count));
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Hephaestus: add this recipe to your queue instead of crafting it now.\nUses the count box; 0 adds one.");
        ImGui.SameLine();

        // Vertically centre the (shorter) input against the button.
        var count = _launcher.Count;
        ImGui.SetNextItemWidth(CountWidth * _scale);
        var inputHeight = ImGui.GetFrameHeight();
        if (_buttonSize.Y > inputHeight)
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + ((_buttonSize.Y - inputHeight) / 2f));
        if (ImGui.InputInt("##hephCraftCount", ref count))
            _launcher.Count = Math.Clamp(count, 0, 9999);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How many to craft. 0 = all.\nCraft never goes past the max your materials and crystals make; + Queue is not limited by it.");

        ImGui.SameLine();
        ImGui.SetCursorPosY(0f);

        var canStart = _launcher.CanStart(observation.SelectedRecipeId, observation.Craftable);
        if (GoldButton(_launcher.Label(observation.Craftable), _buttonSize, _scale, canStart, primary: true))
            _launcher.Start(observation.SelectedRecipeId, observation.Craftable);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Hephaestus: craft this recipe repeatedly.\nStarts each craft, checks the item arrived, pauses if gear needs repair.\n"
                             + $"Your materials and crystals make {Math.Max(0, observation.Craftable)}.");
    }

    private Vector2 FullRowSize()
        => new(((QueueWidth + CountWidth) * _scale) + (2f * Gap * _scale) + _buttonSize.X, _buttonSize.Y);

    /// <summary>
    /// A button drawn to look like the game's own gold "Synthesize" button: rounded gold fill, dark
    /// border, dark text, and a top highlight to fake the bevel. Click is by hover-rect, so the id
    /// does not need a "##" suffix.
    /// </summary>
    private static bool GoldButton(string label, Vector2 size, float scale, bool enabled, bool primary = true)
    {
        var origin = ImGui.GetCursorScreenPos();
        var end = origin + size;
        var dl = ImGui.GetWindowDrawList();
        var hovered = enabled && ImGui.IsMouseHoveringRect(origin, end);
        var down = hovered && ImGui.IsMouseDown(ImGuiMouseButton.Left);

        var mult = primary ? 1f : 0.92f;
        var fill = !enabled ? GoldDisabled
            : down ? GoldActive * mult
            : hovered ? GoldHover * mult
            : GoldFill * mult;

        var rounding = size.Y * 0.5f;
        dl.AddRectFilled(origin, end, ImGui.GetColorU32(fill), rounding);
        dl.AddRect(origin, end, ImGui.GetColorU32(enabled ? GoldBorder : new Vector4(0.16f, 0.15f, 0.12f, 1f)), rounding, ImDrawFlags.None, 1f * scale);
        if (enabled)
            dl.AddLine(origin + new Vector2(rounding, 1f * scale), new Vector2(end.X - rounding, origin.Y + 1f * scale),
                ImGui.GetColorU32(GoldHighlight), 1f * scale);

        var textColor = enabled ? GoldText : new Vector4(0.50f, 0.48f, 0.42f, 1f);
        var ts = ImGui.CalcTextSize(label);
        dl.AddText(origin + (size - ts) * 0.5f, ImGui.GetColorU32(textColor), label);

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(size);

        if (!enabled)
            return false;
        if (hovered)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        return hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
    }
}
