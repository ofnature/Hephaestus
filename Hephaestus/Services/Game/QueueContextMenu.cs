using System;
using System.Collections.Generic;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Hephaestus.Features.Queue;

namespace Hephaestus.Services.Game;

/// <summary>
/// "[H] Add to Hephaestus Queue ▸" on the game's right-click menus, for any item that can be
/// crafted — with a submenu of quantities, since a context menu cannot ask for a number.
///
/// WHICH item was right-clicked is the hard part: every window keeps it somewhere else. Other
/// plugins carry a table of raw per-window memory offsets for this, which silently rots with each
/// game patch. This one uses typed sources only, and simply offers nothing where it cannot be sure:
///   - inventory-style menus: Dalamud hands over the item itself;
///   - the crafting log: AgentRecipeNote.ContextMenuResultItemId (FFXIVClientStructs), which covers
///     the recipe list AND the ingredient rows — so a sub-craft can be queued straight from its parent;
///   - anything else (chat links, market board, shops, ...): the item under the cursor when the menu
///     opened, from IGameGui.HoveredItem.
/// </summary>
public sealed unsafe class QueueContextMenu : IDisposable
{
    private static readonly int[] Quantities = { 1, 3, 5, 10, 20, 50, 99 };

    /// <summary>HoveredItem: event items start here; below it, +1,000,000 marks HQ and +500,000 a collectable.</summary>
    private const ulong FirstEventItem = 2_000_000;
    private const ulong ItemIdModulus = 500_000;

    /// <summary>UIColor row for the prefix letter.</summary>
    private const ushort PrefixColor = 706;

    private readonly IContextMenu _contextMenu;
    private readonly IGameGui _gameGui;
    private readonly RecipeInfoReader _recipes;
    private readonly Func<bool> _enabled;
    private readonly Func<uint> _currentJob;
    private readonly Action<uint, int> _addToQueue;
    private readonly IPluginLog _log;

    /// <param name="enabled">Logged in, and the menu entry switched on.</param>
    /// <param name="addToQueue">(recipeId, quantity).</param>
    public QueueContextMenu(
        IContextMenu contextMenu, IGameGui gameGui, RecipeInfoReader recipes,
        Func<bool> enabled, Func<uint> currentJob, Action<uint, int> addToQueue, IPluginLog log)
    {
        _contextMenu = contextMenu;
        _gameGui = gameGui;
        _recipes = recipes;
        _enabled = enabled;
        _currentJob = currentJob;
        _addToQueue = addToQueue;
        _log = log;

        _contextMenu.OnMenuOpened += OnMenuOpened;
    }

    public void Dispose() => _contextMenu.OnMenuOpened -= OnMenuOpened;

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        try
        {
            if (!_enabled())
                return;

            var (itemId, contextJob) = Resolve(args);
            if (itemId == 0)
                return;

            if (RecipeChoice.Pick(_recipes.RecipesFor(itemId), contextJob, _currentJob()) is not { } choice)
                return; // not craftable: stay out of the menu

            var recipeId = choice.RecipeId;
            args.AddMenuItem(new MenuItem
            {
                Name = "Add to Hephaestus Queue",
                PrefixChar = 'H',
                PrefixColor = PrefixColor,
                IsSubmenu = true,
                OnClicked = clicked => clicked.OpenSubmenu(QuantityMenu(recipeId)),
            });
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Hephaestus: context menu failed"); // never break the game's own menu
        }
    }

    private List<MenuItem> QuantityMenu(uint recipeId)
    {
        var items = new List<MenuItem>(Quantities.Length);
        foreach (var quantity in Quantities)
        {
            var count = quantity;
            items.Add(new MenuItem
            {
                Name = $"× {count}",
                PrefixChar = 'H',
                PrefixColor = PrefixColor,
                OnClicked = _ => _addToQueue(recipeId, count),
            });
        }

        return items;
    }

    /// <returns>The right-clicked item (0 = unknown) and the crafting job the menu implies (0 = none).</returns>
    private (uint ItemId, uint ContextJob) Resolve(IMenuOpenedArgs args)
    {
        if (args.MenuType == ContextMenuType.Inventory)
            return (args.Target is MenuTargetInventory { TargetItem: { } item } ? item.BaseItemId : 0u, 0u);

        if (args.AddonName == "RecipeNote")
        {
            var agent = AgentRecipeNote.Instance();
            if (agent == null)
                return (0, 0);

            // SelectedCraftType 0..7 = Carpenter..Culinarian: the job tab being browsed.
            var job = agent->SelectedCraftType is >= 0 and <= 7 ? (uint)agent->SelectedCraftType + 8u : 0u;
            return (agent->ContextMenuResultItemId, job);
        }

        var hovered = _gameGui.HoveredItem;
        return hovered is 0 or >= FirstEventItem ? (0u, 0u) : ((uint)(hovered % ItemIdModulus), 0u);
    }
}
