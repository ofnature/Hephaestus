using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Hephaestus.Features.Materials;
using Hephaestus.Services;

namespace Hephaestus.Windows;

/// <summary>
/// The Buy List and Gather List tabs (user, 2026-09-21): a snapshot of what the queue needs from one
/// source — Needed against what is in the bags — so it can be fetched by hand, and at the bottom the
/// buttons that fetch it: Auto purchase; or Send to Demeter (hand over, no start) and Auto gather.
/// </summary>
internal static class SupplyListTab
{
    public static void Draw(SupplyDesk desk, SupplyKind kind)
    {
        var buy = kind == SupplyKind.Buy;
        var list = buy ? desk.BuyList : desk.GatherList;
        var noun = buy ? "buy" : "gather";

        if (list == null)
        {
            ImGui.TextColored(HephaestusTheme.TextSecondary, buy
                ? "No buy list yet. \"Make buy list\" on the Queue tab lists everything the queue needs that NPC vendors sell."
                : "No gather list yet. \"Make gather list\" on the Queue tab lists what the queue needs that vendors do not cover and Demeter can gather.");
            if (ImGui.Button($"Make {noun} list now"))
                desk.Make(kind);
            return;
        }

        ImGui.TextColored(HephaestusTheme.TextSecondary, $"Made at {list.MadeAt.ToLocalTime():HH:mm} from the queue.");
        ImGui.SameLine();
        if (ImGui.SmallButton("Refresh"))
            desk.Make(kind);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Remake it from the queue as it is now. Amounts are fixed between refreshes; \"In bags\" follows your inventory.\n"
                             + "Bought sub-combines (ingots on the market board, say)? Refresh, and what they replace drops off.");
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear"))
        {
            desk.Clear(kind);
            return;
        }

        if (!buy && !desk.DemeterAvailable)
            ImGui.TextColored(HephaestusTheme.StatusYellow, "Demeter is not loaded: nothing can go to gathering, so it shows below as unobtainable.");

        DrawLines(desk, list, buy);
        DrawUnobtainable(desk, list);
        if (buy)
            DrawIntermediates(desk, list);

        ImGui.Separator();
        if (buy)
            DrawBuyFooter(desk, list);
        else
            DrawGatherFooter(desk);
    }

    private static void DrawLines(SupplyDesk desk, SupplyList list, bool buy)
    {
        if (list.Lines.Count == 0)
        {
            ImGui.TextColored(HephaestusTheme.TextSecondary, buy
                ? "Nothing the queue needs is sold by a vendor — or your bags already hold it."
                : "Nothing to gather.");
            return;
        }

        var columns = buy ? 6 : 4;
        if (!ImGui.BeginTable(buy ? "buyList" : "gatherList", columns, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Needed", ImGuiTableColumnFlags.WidthFixed, 60f);
        ImGui.TableSetupColumn("In bags", ImGuiTableColumnFlags.WidthFixed, 60f);
        ImGui.TableSetupColumn("Missing", ImGuiTableColumnFlags.WidthFixed, 60f);
        if (buy)
        {
            ImGui.TableSetupColumn("Each", ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableSetupColumn("Cost", ImGuiTableColumnFlags.WidthFixed, 80f);
        }

        ImGui.TableHeadersRow();

        var notes = buy ? desk.BuyNotes : desk.GatherNotes;
        foreach (var line in list.Lines)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(line.Done ? HephaestusTheme.StatusGreen : HephaestusTheme.TextPrimary, line.Name);
            if (ImGui.IsItemHovered())
            {
                var tip = "For: " + string.Join(", ", line.For);
                if (buy)
                    tip += "\nSold by: " + string.Join(", ", desk.SellersOf(line.ItemId).Take(8));
                ImGui.SetTooltip(tip);
            }

            if (!line.Done && notes.TryGetValue(line.ItemId, out var note))
                ImGui.TextColored(HephaestusTheme.StatusYellow, $"   last try: {note}");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(line.Needed.ToString("N0"));
            ImGui.TableNextColumn();
            ImGui.TextColored(line.Done ? HephaestusTheme.StatusGreen : HephaestusTheme.TextSecondary, line.InBags.ToString("N0"));
            ImGui.TableNextColumn();
            ImGui.TextColored(line.Done ? HephaestusTheme.StatusGrey : HephaestusTheme.StatusYellow, line.Missing.ToString("N0"));

            if (buy)
            {
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(line.UnitPrice.ToString("N0"));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(((long)line.Missing * line.UnitPrice).ToString("N0"));
            }
        }

        ImGui.EndTable();
    }

    private static void DrawUnobtainable(SupplyDesk desk, SupplyList list)
    {
        if (list.Unobtainable.Count == 0)
            return;

        ImGui.Spacing();
        ImGui.TextColored(HephaestusTheme.StatusYellow, desk.FreeTrial
            ? "Not sold by a vendor (or not affordable) and not gatherable — the queue stays short of these:"
            : "Not sold by a vendor (or not affordable) and not gatherable — buy these on the market board yourself, then Refresh:");
        foreach (var line in list.Unobtainable)
            ImGui.TextColored(HephaestusTheme.TextSecondary, $"   {line.Missing:N0} × {line.Name}  —  for {string.Join(", ", line.For)}");
    }

    /// <summary>What the queue will craft on the way. A paid account may buy these instead — by hand, never automated.</summary>
    private static void DrawIntermediates(SupplyDesk desk, SupplyList list)
    {
        if (list.Intermediates.Count == 0)
            return;

        ImGui.Spacing();
        ImGui.TextColored(HephaestusTheme.TextPrimary, desk.FreeTrial
            ? "Crafted by the queue on the way (the materials above include theirs):"
            : "Crafted by the queue on the way — or buy them on the market board yourself, then Refresh, and their materials drop off:");
        foreach (var line in list.Intermediates)
            ImGui.TextColored(HephaestusTheme.TextSecondary, $"   {line.Quantity:N0} × {line.Name}  —  for {string.Join(", ", line.For)}");
    }

    private static void DrawBuyFooter(SupplyDesk desk, SupplyList list)
    {
        var cost = list.CostOfMissing;
        ImGui.TextColored(
            cost > desk.Gil ? HephaestusTheme.StatusYellow : HephaestusTheme.TextSecondary,
            $"Still to buy: {cost:N0} gil. You hold {desk.Gil:N0}.");

        if (desk.Buying)
        {
            ImGui.TextColored(HephaestusTheme.StatusGreen, desk.BuyStatus);
            if (ImGui.Button("Stop##buy"))
                desk.StopAll();
            return;
        }

        ImGui.BeginDisabled(!desk.CanAutoPurchase);
        if (ImGui.Button("Auto purchase", new Vector2(160f, 0f)))
            desk.AutoPurchase();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(list.AllDone ? "Everything on the list is in your bags."
                : desk.Busy ? "The queue, a craft run or gathering has the character right now."
                : "Buys everything still missing: from a vendor near you first, else walks (vnavmesh/Ariadne) or teleports (Lifestream) to one.\n"
                  + "Each purchase is checked against your bags. Bags full stops it.");
        }

        ImGui.SameLine();
        ImGui.TextColored(HephaestusTheme.StatusGrey, desk.BuyStatus);
    }

    private static void DrawGatherFooter(SupplyDesk desk)
    {
        if (desk.Gathering)
        {
            ImGui.TextColored(HephaestusTheme.StatusGreen, desk.GatherStatus);
            if (ImGui.Button("Stop##gather"))
                desk.StopAll();
            return;
        }

        ImGui.BeginDisabled(!desk.CanSend);
        if (ImGui.Button("Send to Demeter", new Vector2(160f, 0f)))
            desk.SendToDemeter();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(!desk.DemeterAvailable ? "Demeter is not loaded."
                : !desk.CanHandOver ? "This Demeter cannot take a list without starting it (no Gather.SetList gate).\nAuto gather works."
                : "Puts this list into Demeter's gather window as the \"Hephaestus\" list, without starting it.");
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(!desk.CanAutoGather);
        if (ImGui.Button("Auto gather", new Vector2(160f, 0f)))
            desk.AutoGather();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Demeter gathers everything still missing now, and stays where it ends.");

        ImGui.TextColored(HephaestusTheme.StatusGrey, desk.GatherStatus);
    }
}
