using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Hephaestus.Features.Workshop;
using Hephaestus.Services.Game;
using Hephaestus.Windows.Components;

namespace Hephaestus.Windows;

/// <summary>
/// The FC-workshop build list: pick a part, see its phases (1, 2, 3…) each with the materials they
/// need, and queue every phase at once. Materials stay grouped by phase so the build is gathered in
/// order, then handed to the craft queue by <paramref name="queuePart"/>.
/// </summary>
public sealed class WorkshopBuildWindow : Window
{
    private readonly CompanyCraftReader _reader;
    private readonly WorkshopBuildStore _store;
    private readonly AccountReader _account;
    private readonly Action<uint> _queuePart;

    private IReadOnlyList<WorkshopPartInfo>? _parts;
    private readonly Dictionary<uint, WorkshopBuild?> _builds = new();
    private uint _selectedPart;

    public WorkshopBuildWindow(
        CompanyCraftReader reader,
        WorkshopBuildStore store,
        AccountReader account,
        Action<uint> queuePart)
        : base("Hephaestus — Workshop##HephaestusWorkshop")
    {
        _reader = reader;
        _store = store;
        _account = account;
        _queuePart = queuePart;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(360, 220),
            MaximumSize = new Vector2(640, 1200),
        };
    }

    public override void Draw()
    {
        HephaestusHeader.DrawCompact("Workshop");

        var contentId = _account.LocalContentId;
        if (contentId == 0)
        {
            HephaestusTheme.Text("Log in to a character to build.", HephaestusTheme.TextSecondary);
            return;
        }

        DrawPicker(contentId);
        HephaestusTheme.VSpace(8f);
        DrawBuildList(contentId);
    }

    private void DrawPicker(ulong contentId)
    {
        HephaestusTheme.SectionLabel("Add a part");
        _parts ??= _reader.AllParts();

        var preview = _parts.FirstOrDefault(p => p.ItemId == _selectedPart)?.Name ?? "Choose a part…";
        ImGui.SetNextItemWidth(Math.Max(200f, ImGui.GetContentRegionAvail().X - 118f * ImGuiHelpers.GlobalScale));
        if (ImGui.BeginCombo("##workshopPart", preview))
        {
            var lastType = string.Empty;
            foreach (var part in _parts)
            {
                if (part.TypeName != lastType)
                {
                    lastType = part.TypeName;
                    ImGui.TextColored(HephaestusTheme.TextDim, part.TypeName);
                }

                if (ImGui.Selectable($"{part.Name}##{part.ItemId}", part.ItemId == _selectedPart))
                    _selectedPart = part.ItemId;
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (Buttons.Action("Add", _selectedPart != 0))
        {
            _store.Add(contentId, _selectedPart);
            _builds.Remove(_selectedPart);
            _selectedPart = 0;
        }
    }

    private void DrawBuildList(ulong contentId)
    {
        var entries = _store.Load(contentId);
        if (entries.Count == 0)
        {
            HephaestusTheme.Text("No parts yet — add one above.", HephaestusTheme.TextMuted);
            return;
        }

        HephaestusTheme.SectionLabel("Build list");

        uint? remove = null;
        foreach (var entry in entries)
        {
            if (DrawPart(entry.PartItemId, BuildFor(entry.PartItemId)))
                remove = entry.PartItemId;
        }

        if (remove is { } partId)
        {
            _store.Remove(contentId, partId);
            _builds.Remove(partId);
        }
    }

    private WorkshopBuild? BuildFor(uint partItemId)
    {
        if (_builds.TryGetValue(partItemId, out var cached))
            return cached;

        var build = _reader.Build(partItemId);
        _builds[partItemId] = build;
        return build;
    }

    /// <returns>True when the player asked to remove this part.</returns>
    private bool DrawPart(uint partItemId, WorkshopBuild? build)
    {
        if (build is null)
        {
            HephaestusTheme.Text($"Unknown part {partItemId}.", HephaestusTheme.StatusGrey);
            return false;
        }

        HephaestusTheme.SectionHeader(build.PartName);

        if (Buttons.Action("Queue all phases"))
            _queuePart(partItemId);

        ImGui.SameLine();
        var remove = Buttons.Icon(FontAwesomeIcon.Times, $"##workshopRemove{partItemId}", 24f, "Remove from list", HephaestusTheme.TextMuted);

        foreach (var phase in build.Phases)
        {
            HephaestusTheme.Text($"Phase {phase.Number}", HephaestusTheme.AccentGold);
            foreach (var material in phase.Materials)
            {
                ImGui.Indent();
                HephaestusTheme.Text($"{material.Name} × {material.Count}", HephaestusTheme.TextSecondary);
                ImGui.Unindent();
            }
        }

        HephaestusTheme.VSpace(6f);
        return remove;
    }
}
