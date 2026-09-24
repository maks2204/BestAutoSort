using System;
using System.Collections.Generic;
using BestAutoSort.Runtime;
using BestAutoSort.TxCore;
using UnityEngine;

namespace BestAutoSort.Tx
{
    /// <summary>
    /// RPC names and peer hello/compatibility.
    /// Requests go through the chest ZNetView: no target = to the owner (manager),
    /// with target = to a specific peer (responses).
    ///
    /// Delivery order (no-FIFO assumption): the engine does NOT guarantee that
    /// requests, responses, Queries or presence packets arrive in send order
    /// (UDP transport, relayed fan-out, resends). NOTHING in the protocol depends
    /// on arrival order: every request carries its full identity (txId) and every
    /// outcome is idempotent (Processed-cache replay + per-sender floor gate +
    /// Query-by-txId for lost responses). A reordered duplicate is a cache hit;
    /// a reordered first-timer at/below the floor is Indeterminate, never executed.
    /// </summary>
    internal static class TxNet
    {
        internal const string TxRequestRpc = "BestAutoSort_TxRequest";
        internal const string TxResponseRpc = "BestAutoSort_TxResponse";
        internal const string HelloRpc = "BestAutoSort_MultiUserHello";

        private static readonly HashSet<long> CompatiblePeers = new HashSet<long>();
        private static ZRoutedRpc _registeredRpc;
        private static float _nextHelloAt;

        internal static bool IsCompatiblePeer(long peer)
        {
            return CompatiblePeers.Contains(peer);
        }

        internal static void Reset()
        {
            CompatiblePeers.Clear();
            _registeredRpc = null;
            _nextHelloAt = 0f;
        }

        /// <summary>Call from Update (main thread). Broadcasts hello roughly every 10s.</summary>
        internal static void PumpHello()
        {
            ZRoutedRpc instance = ZRoutedRpc.instance;
            if (instance == null)
                return;
            if (instance != _registeredRpc)
            {
                _registeredRpc = instance;
                CompatiblePeers.Clear();
                instance.Register<ZPackage>(TxFlights.FlightsRpc, TxFlights.OnFlightPacket);
                instance.Register<string>(HelloRpc, delegate (long sender, string version)
                {
                    if (Plugin.IsActive)
                    {
                        // Wave-1 server authority: Hello carries version + mode
                        // ("0.5.16;auth=0"); legacy bare-version peers behave as
                        // LegacyDistributed. Mismatched peers are rejected.
                        ServerAuthorityMode localMode = ServerAuthority.EffectiveMode();
                        if (TxAuthorityHello.AreCompatibleWithHello(Plugin.PluginVersion, localMode, version))
                            CompatiblePeers.Add(sender);
                        else
                            CompatiblePeers.Remove(sender);
                    }
                });
                _nextHelloAt = 0f;
            }
            if (_registeredRpc != null && Time.realtimeSinceStartup >= _nextHelloAt)
            {
                _nextHelloAt = Time.realtimeSinceStartup + 10f;
                string hello = TxAuthorityHello.BuildHello(Plugin.PluginVersion, ServerAuthority.EffectiveMode());
                _registeredRpc.InvokeRoutedRPC(0L, HelloRpc, new object[1] { hello });
            }
        }
    }
}
