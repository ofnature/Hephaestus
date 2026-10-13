using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Hephaestus.Services;

/// <summary>
/// Walking, through vnavmesh — or Ariadne, whose gates mirror vnavmesh's shapes under its own prefix
/// (Odysseus's VnavIpc and Charon's nav client use the same two). Whichever says its mesh is ready
/// answers. Every call fails open: no navigation plugin means "cannot walk", never a throw.
/// </summary>
public sealed class NavClient
{
    private static readonly string[] Providers = { "vnavmesh", "Ariadne" };

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly Dictionary<string, object> _subscribers = new();

    public NavClient(IDalamudPluginInterface pluginInterface)
    {
        _pluginInterface = pluginInterface;
    }

    /// <summary>The plugin that last reported a ready mesh; null when none has.</summary>
    public string? Provider { get; private set; }

    /// <summary>A navigation plugin is loaded and has a mesh for this zone.</summary>
    public bool Ready
    {
        get
        {
            foreach (var provider in Providers)
            {
                if (Try(() => BoolGate($"{provider}.Nav.IsReady").InvokeFunc()))
                {
                    Provider = provider;
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>A path is being computed or followed.</summary>
    public bool Busy => Provider is { } p
                        && (Try(() => BoolGate($"{p}.SimpleMove.PathfindInProgress").InvokeFunc())
                            || Try(() => BoolGate($"{p}.Path.IsRunning").InvokeFunc()));

    /// <summary>Path to within <paramref name="tolerance"/> of a point and start walking.</summary>
    public bool MoveCloseTo(Vector3 destination, float tolerance)
        => Provider is { } p && Try(() =>
            Subscriber<ICallGateSubscriber<Vector3, bool, float, bool>>(
                    $"{p}.SimpleMove.PathfindAndMoveCloseTo", n => _pluginInterface.GetIpcSubscriber<Vector3, bool, float, bool>(n))
                .InvokeFunc(destination, false, tolerance));

    public void Stop()
    {
        if (Provider is not { } p)
            return;

        Try(() =>
        {
            Subscriber<ICallGateSubscriber<object>>($"{p}.Path.Stop", n => _pluginInterface.GetIpcSubscriber<object>(n)).InvokeAction();
            return true;
        });
    }

    private ICallGateSubscriber<bool> BoolGate(string gate)
        => Subscriber<ICallGateSubscriber<bool>>(gate, n => _pluginInterface.GetIpcSubscriber<bool>(n));

    private TSub Subscriber<TSub>(string gate, Func<string, TSub> make)
        where TSub : class
    {
        if (!_subscribers.TryGetValue(gate, out var subscriber))
            _subscribers[gate] = subscriber = make(gate);
        return (TSub)subscriber;
    }

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
