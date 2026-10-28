using System;
using System.Collections.Generic;
using System.Linq;
using Hephaestus.Features.Crafting;
using Hephaestus.Features.Queue;

namespace Hephaestus.Features.Materials;

/// <summary>An intermediate the queue makes before the entries that need it.</summary>
/// <param name="Crafts">Syntheses, not items — a recipe can yield several per craft.</param>
/// <param name="Depth">1 = a direct ingredient of a list entry; larger = further down the tree.</param>
/// <param name="For">The list entries whose demand this serves.</param>
public sealed record PlannedSubCraft(RecipeInfo Recipe, int Crafts, int Depth, IReadOnlyList<CraftListItem> For);

/// <summary>A material neither the bags nor any sub-craft can cover — it has to come from somewhere else.</summary>
/// <param name="ByEntry">How much of it each list entry accounts for.</param>
public sealed record MaterialShortfall(uint ItemId, string Name, int Missing, IReadOnlyDictionary<CraftListItem, int> ByEntry);

/// <summary>NPC vendors as a source of materials.</summary>
/// <param name="UnitPrice">What one of an item costs at a vendor we know how to buy from; 0 = not sold.</param>
/// <param name="Gil">The gil there is to spend. A free-trial character holds at most 300,000.</param>
public sealed record VendorSupply(Func<uint, uint> UnitPrice, long Gil);

/// <summary>Something to buy from a vendor: always NQ, always exactly the live shortfall.</summary>
public sealed record PlannedPurchase(uint ItemId, string Name, int Quantity, uint UnitPrice, IReadOnlyDictionary<CraftListItem, int> ByEntry)
{
    public long Cost => (long)Quantity * UnitPrice;
}

/// <summary>What a craft list needs beyond what is in the bags, as of one moment. Never stored: replanned every time it is needed.</summary>
public sealed class MaterialPlan
{
    public static readonly MaterialPlan Empty = new(
        Array.Empty<PlannedSubCraft>(), Array.Empty<PlannedPurchase>(), Array.Empty<PlannedGather>(), Array.Empty<MaterialShortfall>(),
        Array.Empty<PlannedWithdrawal>(), Array.Empty<PlannedChestWithdrawal>(),
        new Dictionary<CraftListItem, int>(), shortOfGil: false);

    private readonly IReadOnlyDictionary<CraftListItem, int> _achievable;

    public MaterialPlan(
        IReadOnlyList<PlannedSubCraft> subCrafts, IReadOnlyList<PlannedPurchase> purchases, IReadOnlyList<PlannedGather> gathers,
        IReadOnlyList<MaterialShortfall> shortfalls, IReadOnlyList<PlannedWithdrawal> withdrawals,
        IReadOnlyList<PlannedChestWithdrawal> chestWithdrawals, IReadOnlyDictionary<CraftListItem, int> achievable, bool shortOfGil)
    {
        Withdrawals = withdrawals;
        ChestWithdrawals = chestWithdrawals;
        SubCrafts = subCrafts;
        Purchases = purchases;
        Gathers = gathers;
        Shortfalls = shortfalls;
        _achievable = achievable;
        ShortOfGil = shortOfGil;
    }

    /// <summary>In the order they must be made: deepest first, so nothing is started before what it needs.</summary>
    public IReadOnlyList<PlannedSubCraft> SubCrafts { get; }

    /// <summary>What to buy from vendors; empty when the plan was made without them.</summary>
    public IReadOnlyList<PlannedPurchase> Purchases { get; }

    public long PurchaseCost => Purchases.Sum(p => p.Cost);

    /// <summary>What to take from the company chest first of all; empty when the plan was made without it.</summary>
    public IReadOnlyList<PlannedChestWithdrawal> ChestWithdrawals { get; }

    /// <summary>What to take from the character's retainers first; empty when the plan was made without them.</summary>
    public IReadOnlyList<PlannedWithdrawal> Withdrawals { get; }

    /// <summary>What to ask Demeter to gather; empty when the plan was made without it.</summary>
    public IReadOnlyList<PlannedGather> Gathers { get; }

    public IReadOnlyList<MaterialShortfall> Shortfalls { get; }

    /// <summary>A vendor sells something that is still short: the gil ran out before it was covered, and it cannot be gathered.</summary>
    public bool ShortOfGil { get; }

    /// <summary>How many of an entry's remaining crafts can be seen through to the end — sub-crafts included — from what is on hand.</summary>
    public int Achievable(CraftListItem entry) => _achievable.TryGetValue(entry, out var count) ? count : 0;

    public IEnumerable<MaterialShortfall> ShortfallsFor(CraftListItem entry) => Shortfalls.Where(s => s.ByEntry.ContainsKey(entry));
}

/// <summary>
/// The shortfall planner: walks each list entry's recipe tree against what is in the bags and
/// answers "what must be crafted first, and what is still missing after that".
///
/// Rules (docs/scoping-decisions.md, Material sourcing):
///   - what is on hand is used first; one bag count serves the WHOLE list, in list order, so two
///     entries never both count on the same ore;
///   - a missing ingredient that this character can craft becomes a sub-craft, sized from the live
///     shortfall of the whole list — recomputed every time, never replayed from a stored batch;
///   - an entry the list makes EARLIER feeds the entries after it (a caller who lists sub-crafts
///     first means it), so it is not planned a second time;
///   - materials are only committed to crafts that can be FINISHED: an entry short of something
///     nothing can supply has its demand cut to the count that can still be seen through, so ingots
///     are never smelted for a shield that is missing its rare drop anyway;
///   - vendors (when the caller offers them as a source) come LAST: only for what cannot be
///     sub-crafted, NQ, exactly the shortfall, within the gil held, in list order — and under the
///     same safety rule, so gil is never spent towards a craft that cannot be finished either;
///   - gathering (Demeter) comes after vendors, for whatever they cannot cover (user, 2026-09-21:
///     "you can always gather it if you can't buy it"). No limit but the same safety rule;
///   - the FC chest (when offered) comes right after the bags, then the retainers — both BEFORE
///     sub-crafting (the source order for paid accounts): an ingot a
///     retainer holds is withdrawn, not smelted again. The index can be stale; the trip judges by
///     what it really withdraws and the next plan sees the bags.
///
/// Pure: bag counts, recipes and "can this character craft that" all arrive as delegates.
/// </summary>
public sealed class MaterialPlanner
{
    /// <summary>Ore -> ingot -> part -> product and one to spare; also bounds a cycle in bad sheet data.</summary>
    public const int MaxDepth = 4;

    private readonly Func<uint, RecipeInfo?> _recipeInfo;
    private readonly Func<uint, IReadOnlyList<RecipeOption>> _recipesFor;
    private readonly Func<RecipeInfo, bool> _canCraft;
    private readonly Func<uint, int> _held;
    private readonly Func<uint, string> _itemName;
    private readonly Dictionary<uint, RecipeInfo?> _infoCache = new(); // sheet data: never changes in a session

    /// <param name="recipesFor">Every recipe that makes an item; empty when it cannot be crafted.</param>
    /// <param name="canCraft">This character could craft the recipe UNATTENDED: a gearset for the job, and the level for it.</param>
    /// <param name="held">How many of an item are in the bags, NQ + HQ (crystals included).</param>
    public MaterialPlanner(
        Func<uint, RecipeInfo?> recipeInfo,
        Func<uint, IReadOnlyList<RecipeOption>> recipesFor,
        Func<RecipeInfo, bool> canCraft,
        Func<uint, int> held,
        Func<uint, string> itemName)
    {
        _recipeInfo = recipeInfo;
        _recipesFor = recipesFor;
        _canCraft = canCraft;
        _held = held;
        _itemName = itemName;
    }

    /// <summary>How many times a recipe can be made right now, from the bags alone.</summary>
    public int CraftableNow(in RecipeInfo recipe)
    {
        Span<(int Need, int Have)> inputs = stackalloc (int, int)[recipe.Inputs.Count];
        for (var i = 0; i < inputs.Length; i++)
            inputs[i] = (recipe.Inputs[i].Amount, _held(recipe.Inputs[i].ItemId));
        return CraftableCount.From(inputs);
    }

    /// <param name="entries">The list, in its own order. Finished and flagged entries are ignored.</param>
    /// <param name="currentJob">Breaks ties between recipes for the same intermediate; 0 = unknown.</param>
    /// <param name="blockedRecipes">Sub-craft recipes that were tried and would not run: not planned again.</param>
    /// <param name="vendors">Count vendors as a source. Only pass this when the purchases will really be made —
    /// a plan that counts on ore nobody buys would smelt ingots for shields it then cannot finish.</param>
    /// <param name="gathering">Count Odysseus's gatherer as a source — same caveat: only when it will really be asked.</param>
    /// <param name="retainers">Count what the retainer index says is held — same caveat: only when a trip will withdraw it.</param>
    /// <param name="chest">Count what the chest store says is on its pages — same caveat.</param>
    public MaterialPlan Plan(
        IEnumerable<CraftListItem> entries, uint currentJob, IReadOnlyCollection<uint>? blockedRecipes = null,
        VendorSupply? vendors = null, GatherSupply? gathering = null, RetainerSupply? retainers = null, ChestSupply? chest = null)
    {
        var pass = new Pass(this, currentJob, blockedRecipes, vendors, gathering, retainers, chest);
        var achievable = new Dictionary<CraftListItem, int>();

        foreach (var entry in entries)
        {
            if (!entry.IsRunnable || Info(entry.RecipeId) is not { } info)
                continue;

            var remaining = entry.Quantity - entry.Crafted;

            // The largest count whose whole tree is covered. Demand only grows with the count, so bisect.
            var count = remaining;
            if (!pass.Covers(info, count))
            {
                var (low, high) = (0, remaining - 1);
                while (low < high)
                {
                    var middle = (low + high + 1) / 2;
                    if (pass.Covers(info, middle))
                        low = middle;
                    else
                        high = middle - 1;
                }

                count = low;
            }

            achievable[entry] = count;
            pass.Commit(info, count, entry);

            // The rest is reported, not reserved: later entries may still use what it would have taken.
            if (count < remaining)
                pass.ReportMissing(info, remaining - count, entry);
        }

        return new MaterialPlan(
            pass.SubCrafts(), pass.Purchases(_itemName), pass.Gathers(_itemName), pass.Shortfalls(_itemName), pass.Withdrawals(_itemName),
            pass.ChestWithdrawals(_itemName), achievable, pass.ShortOfGil);
    }

    /// <summary>How many of an item are in the bags right now.</summary>
    public int Held(uint itemId) => Math.Max(0, _held(itemId));

    private RecipeInfo? Info(uint recipeId)
    {
        if (!_infoCache.TryGetValue(recipeId, out var info))
            _infoCache[recipeId] = info = _recipeInfo(recipeId);
        return info;
    }

    /// <summary>The recipe that makes an intermediate for THIS character: the parent's job first (we are going there anyway), then the current one.</summary>
    private RecipeInfo? RecipeFor(uint itemId, uint parentJob, uint currentJob, IReadOnlyCollection<uint>? blocked)
    {
        var usable = new List<RecipeOption>();
        foreach (var option in _recipesFor(itemId))
        {
            if (blocked?.Contains(option.RecipeId) != true && Info(option.RecipeId) is { } info && _canCraft(info))
                usable.Add(option);
        }

        return RecipeChoice.Pick(usable, parentJob, currentJob) is { } picked ? Info(picked.RecipeId) : null;
    }

    /// <summary>One planning pass: a ledger over the bag counts, and what has been decided so far.</summary>
    private sealed class Pass
    {
        private readonly MaterialPlanner _planner;
        private readonly uint _currentJob;
        private readonly IReadOnlyCollection<uint>? _blocked;
        private readonly VendorSupply? _vendors;
        private readonly GatherSupply? _gathering;
        private readonly RetainerSupply? _retainers;
        private readonly ChestSupply? _chest;
        private readonly Dictionary<uint, int> _chestRead = new();
        private readonly Dictionary<uint, Dictionary<CraftListItem, int>> _chestWithdrawals = new();
        private readonly Dictionary<uint, int> _retainerRead = new();
        private readonly Dictionary<uint, Dictionary<CraftListItem, int>> _withdrawals = new();
        private readonly Dictionary<uint, Dictionary<CraftListItem, int>> _gathers = new();
        private readonly Dictionary<uint, int> _heldRead = new(); // one bag read per item per pass
        private readonly Dictionary<uint, (uint UnitPrice, Dictionary<CraftListItem, int> ByEntry)> _purchases = new();
        private readonly List<SubCraftTally> _subCrafts = new();
        private readonly Dictionary<uint, Dictionary<CraftListItem, int>> _shortfalls = new();

        /// <summary>What the plan has taken from (-) or will add to (+) each item's bag count.</summary>
        private Dictionary<uint, int> _delta = new();
        private Dictionary<uint, int> _retainerTaken = new();
        private Dictionary<uint, int> _chestTaken = new();
        private long _gilLeft;

        public Pass(
            MaterialPlanner planner, uint currentJob, IReadOnlyCollection<uint>? blocked, VendorSupply? vendors, GatherSupply? gathering,
            RetainerSupply? retainers, ChestSupply? chest)
        {
            _chest = chest;
            _planner = planner;
            _currentJob = currentJob;
            _blocked = blocked;
            _vendors = vendors;
            _gathering = gathering;
            _retainers = retainers;
            _gilLeft = Math.Max(0, vendors?.Gil ?? 0);
        }

        public bool ShortOfGil { get; private set; }

        /// <summary>Could <paramref name="crafts"/> of a recipe be seen through, on top of everything already committed? Changes nothing.</summary>
        public bool Covers(in RecipeInfo info, int crafts)
        {
            var (saved, savedTaken, savedChest, savedGil) = (_delta, _retainerTaken, _chestTaken, _gilLeft);
            _delta = new Dictionary<uint, int>(saved);
            _retainerTaken = new Dictionary<uint, int>(savedTaken);
            _chestTaken = new Dictionary<uint, int>(savedChest);
            var missing = DemandInputs(info, crafts, depth: 1, entry: null, record: false);
            (_delta, _retainerTaken, _chestTaken, _gilLeft) = (saved, savedTaken, savedChest, savedGil);
            return missing == 0;
        }

        public void Commit(in RecipeInfo info, int crafts, CraftListItem entry)
        {
            if (crafts <= 0)
                return;

            DemandInputs(info, crafts, depth: 1, entry, record: true);

            // What this entry makes is there for the entries AFTER it.
            _delta[info.ResultItemId] = _delta.GetValueOrDefault(info.ResultItemId) + (crafts * Math.Max(1, info.Yield));
        }

        /// <summary>Name what the uncovered part of an entry is short of, without reserving anything for it.</summary>
        public void ReportMissing(in RecipeInfo info, int crafts, CraftListItem entry)
        {
            var (saved, savedTaken, savedChest, savedGil) = (_delta, _retainerTaken, _chestTaken, _gilLeft);
            _delta = new Dictionary<uint, int>(saved);
            _retainerTaken = new Dictionary<uint, int>(savedTaken);
            _chestTaken = new Dictionary<uint, int>(savedChest);
            DemandInputs(info, crafts, depth: 1, entry, record: false);
            (_delta, _retainerTaken, _chestTaken, _gilLeft) = (saved, savedTaken, savedChest, savedGil);
        }

        public IReadOnlyList<PlannedSubCraft> SubCrafts()
            => _subCrafts
                .OrderByDescending(s => s.Depth) // an ingredient always sits deeper than what it goes into; stable for the rest
                .Select(s => new PlannedSubCraft(s.Recipe, s.Crafts, s.Depth, s.For))
                .ToList();

        public IReadOnlyList<PlannedPurchase> Purchases(Func<uint, string> itemName)
            => _purchases.Select(p => new PlannedPurchase(p.Key, itemName(p.Key), p.Value.ByEntry.Values.Sum(), p.Value.UnitPrice, p.Value.ByEntry)).ToList();

        public IReadOnlyList<PlannedChestWithdrawal> ChestWithdrawals(Func<uint, string> itemName)
            => _chestWithdrawals.Select(w => new PlannedChestWithdrawal(w.Key, itemName(w.Key), w.Value.Values.Sum(), w.Value)).ToList();

        public IReadOnlyList<PlannedWithdrawal> Withdrawals(Func<uint, string> itemName)
            => _withdrawals.Select(w => new PlannedWithdrawal(w.Key, itemName(w.Key), w.Value.Values.Sum(), w.Value)).ToList();

        public IReadOnlyList<PlannedGather> Gathers(Func<uint, string> itemName)
            => _gathers.Select(g => new PlannedGather(g.Key, itemName(g.Key), g.Value.Values.Sum(), g.Value)).ToList();

        public IReadOnlyList<MaterialShortfall> Shortfalls(Func<uint, string> itemName)
            => _shortfalls.Select(s => new MaterialShortfall(s.Key, itemName(s.Key), s.Value.Values.Sum(), s.Value)).ToList();

        private int DemandInputs(in RecipeInfo info, int crafts, int depth, CraftListItem? entry, bool record)
        {
            var missing = 0;
            foreach (var input in info.Inputs)
            {
                if (input.ItemId != 0 && input.Amount > 0)
                    missing += Demand(input.ItemId, input.Amount * crafts, depth, info.JobId, entry, record);
            }

            return missing;
        }

        /// <returns>How much of the demand nothing could cover.</returns>
        private int Demand(uint itemId, int quantity, int depth, uint parentJob, CraftListItem? entry, bool record)
        {
            var available = Available(itemId);
            var used = Math.Min(available, quantity);
            _delta[itemId] = _delta.GetValueOrDefault(itemId) - used;
            quantity -= used;
            if (quantity == 0)
                return 0;

            // Then the company chest: what its store says is on the pages, before anything is crafted for it.
            if (_chest != null)
            {
                if (!_chestRead.TryGetValue(itemId, out var onPages))
                    _chestRead[itemId] = onPages = Math.Max(0, _chest.Held(itemId));
                var fromChest = Math.Min(quantity, onPages - _chestTaken.GetValueOrDefault(itemId));
                if (fromChest > 0)
                {
                    _chestTaken[itemId] = _chestTaken.GetValueOrDefault(itemId) + fromChest;
                    quantity -= fromChest;
                    if (record && entry != null)
                        Tally(_chestWithdrawals, itemId, fromChest, entry);
                    if (quantity == 0)
                        return 0;
                }
            }

            // Then the retainers: what the index says they hold, before anything is crafted for it.
            if (_retainers != null)
            {
                if (!_retainerRead.TryGetValue(itemId, out var stored))
                    _retainerRead[itemId] = stored = Math.Max(0, _retainers.Held(itemId));
                var fromRetainers = Math.Min(quantity, stored - _retainerTaken.GetValueOrDefault(itemId));
                if (fromRetainers > 0)
                {
                    _retainerTaken[itemId] = _retainerTaken.GetValueOrDefault(itemId) + fromRetainers;
                    quantity -= fromRetainers;
                    if (record && entry != null)
                        Tally(_withdrawals, itemId, fromRetainers, entry);
                    if (quantity == 0)
                        return 0;
                }
            }

            if (depth > MaxDepth || _planner.RecipeFor(itemId, parentJob, _currentJob, _blocked) is not { } recipe)
            {
                // Then a vendor, for as much as the gil allows...
                var soldButUnpaid = false;
                if (_vendors?.UnitPrice(itemId) is > 0 and var price)
                {
                    var bought = (int)Math.Min(quantity, _gilLeft / price);
                    _gilLeft -= (long)bought * price;
                    quantity -= bought;
                    if (record && entry != null && bought > 0)
                        Tally(_purchases, itemId, price, bought, entry);

                    if (quantity == 0)
                        return 0;

                    soldButUnpaid = true;
                }

                // ...and last, gathering: whatever could not be bought.
                if (_gathering?.CanGather(itemId) == true)
                {
                    if (record && entry != null)
                        Tally(_gathers, itemId, quantity, entry);
                    return 0;
                }

                if (soldButUnpaid && entry != null)
                    ShortOfGil = true;

                // Reported whether or not anything is being reserved: entry != null is what makes it a report.
                if (entry != null)
                    Tally(itemId, quantity, entry);
                return quantity;
            }

            var yield = Math.Max(1, recipe.Yield);
            var crafts = (quantity + yield - 1) / yield;
            _delta[itemId] = _delta.GetValueOrDefault(itemId) + (crafts * yield) - quantity; // a multi-yield craft leaves spares

            if (record && entry != null)
                Tally(recipe, crafts, depth, entry);

            return DemandInputs(recipe, crafts, depth + 1, entry, record);
        }

        private int Available(uint itemId)
        {
            if (!_heldRead.TryGetValue(itemId, out var held))
                _heldRead[itemId] = held = Math.Max(0, _planner._held(itemId));
            return held + _delta.GetValueOrDefault(itemId);
        }

        private void Tally(in RecipeInfo recipe, int crafts, int depth, CraftListItem entry)
        {
            var recipeId = recipe.RecipeId;
            var tally = _subCrafts.Find(s => s.Recipe.RecipeId == recipeId);
            if (tally == null)
                _subCrafts.Add(tally = new SubCraftTally(recipe));

            tally.Crafts += crafts;
            tally.Depth = Math.Max(tally.Depth, depth);
            if (!tally.For.Contains(entry))
                tally.For.Add(entry);
        }

        private static void Tally(
            Dictionary<uint, (uint UnitPrice, Dictionary<CraftListItem, int> ByEntry)> purchases, uint itemId, uint unitPrice, int bought, CraftListItem entry)
        {
            if (!purchases.TryGetValue(itemId, out var purchase))
                purchases[itemId] = purchase = (unitPrice, new Dictionary<CraftListItem, int>());

            purchase.ByEntry[entry] = purchase.ByEntry.GetValueOrDefault(entry) + bought;
        }

        private static void Tally(Dictionary<uint, Dictionary<CraftListItem, int>> into, uint itemId, int quantity, CraftListItem entry)
        {
            if (!into.TryGetValue(itemId, out var byEntry))
                into[itemId] = byEntry = new Dictionary<CraftListItem, int>();

            byEntry[entry] = byEntry.GetValueOrDefault(entry) + quantity;
        }

        private void Tally(uint itemId, int missing, CraftListItem entry) => Tally(_shortfalls, itemId, missing, entry);

        private sealed class SubCraftTally
        {
            public SubCraftTally(RecipeInfo recipe) => Recipe = recipe;

            public RecipeInfo Recipe { get; }

            public int Crafts { get; set; }

            public int Depth { get; set; }

            public List<CraftListItem> For { get; } = new();
        }
    }
}
