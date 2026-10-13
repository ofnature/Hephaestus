using Hephaestus.Features.Crafting.Simulation;

namespace Hephaestus.Features.Crafting.Driving;

/// <summary>What a hotbar slot holds, as far as highlighting cares.</summary>
public enum HotbarCommandKind
{
    Other,

    /// <summary>The Action sheet (ids below 100000) — Manipulation, Innovation, Great Strides, ...</summary>
    Action,

    /// <summary>The CraftAction sheet (ids from 100000) — most crafting actions.</summary>
    CraftAction,
}

/// <summary>
/// Decides whether a hotbar slot is "the recommended action". Pure, because it is subtler than an
/// id compare:
///
///   - an id only means something together with its KIND: a macro or an item can share a number
///     with an action, and must never light up;
///   - a slot has the command the player PUT there and the action it currently APPEARS as. After a
///     successful Hasty Touch the game shows that same slot as Daring Touch — so a recommended
///     Daring Touch has to find the slot the player knows as Hasty Touch, by either route.
/// </summary>
public static class HotbarMatcher
{
    public static HotbarCommandKind KindOf(uint actionId)
        => actionId >= CarpenterActionIds.FirstCraftActionId ? HotbarCommandKind.CraftAction : HotbarCommandKind.Action;

    /// <param name="actionId">The recommended action's id for the CURRENT job; 0 = unknown, never matches.</param>
    /// <param name="fallbackId">A second id that also counts (Hasty Touch's, when Daring Touch is recommended); 0 = none.</param>
    public static bool Matches(
        HotbarCommandKind commandKind, uint commandId,
        HotbarCommandKind apparentKind, uint apparentId,
        uint actionId, uint fallbackId = 0)
        => Is(commandKind, commandId, actionId) || Is(apparentKind, apparentId, actionId)
           || Is(commandKind, commandId, fallbackId) || Is(apparentKind, apparentId, fallbackId);

    /// <summary>The slot Daring Touch lives in is the player's Hasty Touch slot.</summary>
    public static CraftAction? FallbackFor(CraftAction action)
        => action == CraftAction.DaringTouch ? CraftAction.HastyTouch : null;

    private static bool Is(HotbarCommandKind kind, uint id, uint wanted)
        => wanted != 0 && id == wanted && kind == KindOf(wanted);
}
