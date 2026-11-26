using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace Hephaestus.Services.Game;

/// <summary>
/// Who is logged in and what kind of account they are on. Thin fail-open adapter — read live from
/// game state rather than configured per box, because game state cannot drift out of date.
/// </summary>
public sealed unsafe class AccountReader
{
    private readonly ICondition _condition;

    public AccountReader(ICondition condition)
    {
        _condition = condition;
    }

    /// <summary>
    /// Free trial cannot trade, use the market board, join an FC or hire retainers, and is capped
    /// at 300k gil — it changes which material sources exist (see Features.Materials.SourcePolicy).
    /// </summary>
    public bool IsFreeTrial => _condition[ConditionFlag.OnFreeTrial];

    /// <summary>The character's Grand Company, 1-3 (Maelstrom, Twin Adder, Immortal Flames); 0 = none, or unknown.</summary>
    public byte GrandCompany
    {
        get
        {
            try
            {
                var playerState = PlayerState.Instance();
                return playerState == null ? (byte)0 : playerState->GrandCompany;
            }
            catch
            {
                return 0;
            }
        }
    }

    /// <summary>The logged-in character's content id (0 when not logged in) — the stable
    /// per-character key for settings overrides and the craft queue file.</summary>
    public ulong LocalContentId
    {
        get
        {
            try
            {
                var playerState = PlayerState.Instance();
                return playerState == null ? 0 : playerState->ContentId;
            }
            catch
            {
                return 0;
            }
        }
    }
}
