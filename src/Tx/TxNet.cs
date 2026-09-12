using System;
using System.Collections.Generic;
using BestAutoSort.Runtime;
using UnityEngine;

namespace BestAutoSort.Tx
{
    /// <summary>
    /// RPC names and peer hello/compatibility.
    /// Requests go through the chest ZNetView: no target = to the owner (manager),
    /// with target = to a specific peer (responses).
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
                instance.Register<string>(HelloRpc, delegate (long sender, string version)
                {
                    if (Plugin.IsActive)
                    {
                        if (string.Equals(version, Plugin.PluginVersion, StringComparison.Ordinal))
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
                _registeredRpc.InvokeRoutedRPC(0L, HelloRpc, new object[1] { Plugin.PluginVersion });
            }
        }
    }
}
