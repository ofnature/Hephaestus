using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Hephaestus.Services;

/// <summary>
/// Teleports and city aethernet hops, through Lifestream — the gates Odysseus's LifestreamIpc has
/// field-verified (<c>Lifestream.Teleport</c>, <c>AethernetTeleportByPlaceNameId</c>, <c>IsBusy</c>,
/// <c>Abort</c>). The hop is addressed by PlaceName row, not by display text: a name that quietly
/// matches nothing returns true and stands still (Odysseus learned that one). Fail-open throughout.
/// </summary>
public sealed class LifestreamClient
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private ICallGateSubscriber<uint, byte, bool>? _teleport;
    private ICallGateSubscriber<uint, bool>? _aethernet;
    private ICallGateSubscriber<bool>? _isBusy;
    private ICallGateSubscriber<object>? _abort;

    public LifestreamClient(IDalamudPluginInterface pluginInterface)
    {
        _pluginInterface = pluginInterface;
    }

    /// <summary>Teleport to an aetheryte. Lifestream handles the confirmation and queueing.</summary>
    public bool Teleport(uint aetheryteId)
        => Try(() => (_teleport ??= _pluginInterface.GetIpcSubscriber<uint, byte, bool>("Lifestream.Teleport")).InvokeFunc(aetheryteId, 0));

    /// <summary>Walk to the nearest aethernet shard and hop to the stop named by this PlaceName row.</summary>
    public bool AethernetHop(uint placeNameId)
        => Try(() => (_aethernet ??= _pluginInterface.GetIpcSubscriber<uint, bool>("Lifestream.AethernetTeleportByPlaceNameId")).InvokeFunc(placeNameId));

    /// <summary>Lifestream is loaded (its gates exist).</summary>
    public bool Loaded
    {
        get
        {
            try
            {
                return (_isBusy ??= _pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy")).HasFunction;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Lifestream is mid-task (walking to a shard, waiting on a teleport).</summary>
    public bool Busy => Try(() => (_isBusy ??= _pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy")).InvokeFunc());

    public void Abort()
        => Try(() =>
        {
            (_abort ??= _pluginInterface.GetIpcSubscriber<object>("Lifestream.Abort")).InvokeAction();
            return true;
        });

    private static bool Try(Func<bool> call)
    {
        try
        {
            return call();
        }
        catch
        {
            return false;
        }
    }
}
