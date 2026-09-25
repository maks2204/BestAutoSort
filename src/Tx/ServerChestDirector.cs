using System;
using BestAutoSort.Runtime;
using UnityEngine;

namespace BestAutoSort.Tx
{
    /// <summary>
    /// Option-B (server-side manager) slice 1: instance-independent transport.
    ///
    /// Why this exists (proven by decompilation, Valheim l-1.0.16): a dedicated
    /// server never instantiates GameObjects near remote players (ZNetScene only
    /// builds around its single reference position; peer areas are data-only
    /// Ghost zones), so per-instance ZNetView RPC handlers (TxRequestRpc /
    /// TxResponseRpc registered in OnContainerAwake) can never fire server-side.
    /// ZRoutedRpc ALSO dispatches instance-independent handlers when the routed
    /// target ZDO is None (HelloRpc precedent) — this director uses two global
    /// RPCs addressed by chest ZDOID carried in the payload:
    ///   client -(BestAutoSort_TxServerRequest)-> server (2-arg overload = server peer)
    ///   server -(BestAutoSort_TxServerResponse)-> sender peer (explicit target)
    ///
    /// Slice 1 carries Ping only (transport liveness proof, zero GameObjects).
    /// Tx kinds (slice 3+) answer fail-closed until implemented: unknown kinds
    /// are logged and dropped (sender times out to Indeterminate — never applied).
    /// No existing path is touched: view-routed RPCs keep working unchanged.
    /// </summary>
    internal static class ServerChestDirector
    {
        internal const string ServerRequestRpc = "BestAutoSort_TxServerRequest";
        internal const string ServerResponseRpc = "BestAutoSort_TxServerResponse";

        internal const byte KindPing = 0;
        internal const byte KindPong = 0;
        internal const byte KindTx = 1;

        /// <summary>
        /// Client addressing for managed chests in authority mode: wraps the
        /// view-framed request ([txId][payload]) with the chest ZDOID and sends
        /// instance-independent to the server peer (2-arg overload).
        /// </summary>
        internal static bool SubmitToServer(ZDOID chestId, ZPackage request)
        {
            try
            {
                if (chestId.IsNone() || request == null)
                    return false;
                ZRoutedRpc rpc = null;
                try { rpc = ZRoutedRpc.instance; } catch { rpc = null; }
                if (rpc == null)
                    return false;
                ZPackage outer = new ZPackage();
                try
                {
                    outer.Write(chestId);
                    outer.Write(request);
                }
                catch
                {
                    return false;
                }
                try { rpc.InvokeRoutedRPC(ServerRequestRpc, new object[1] { outer }); } catch { return false; }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private const float PingInterval = 60f;
        private static float _nextPingAt;
        private static bool _pongSeen;

        internal static void Reset()
        {
            _nextPingAt = 0f;
            _pongSeen = false;
        }

        internal static void RegisterGlobal(ZRoutedRpc rpc)
        {
            try
            {
                if (rpc == null)
                    return;
                rpc.Register<ZPackage>(ServerRequestRpc, OnServerRequest);
                rpc.Register<ZPackage>(ServerResponseRpc, OnServerResponse);
            }
            catch
            {
            }
        }

        /// <summary>Client-side heartbeat: prove the server path is reachable.</summary>
        internal static void PumpPing()
        {
            try
            {
                if (!Plugin.IsActive)
                    return;
                float now = Time.realtimeSinceStartup;
                if (now < _nextPingAt)
                    return;
                _nextPingAt = now + PingInterval;
                ZNet net = null;
                try { net = ZNet.instance; } catch { net = null; }
                if ((UnityEngine.Object)net == (UnityEngine.Object)null)
                    return;
                bool isServer = false;
                try { isServer = net.IsServer(); } catch { isServer = false; }
                if (isServer)
                    return;
                bool authority = false;
                try { authority = ServerAuthority.IsAuthorityMode(); } catch { authority = false; }
                if (!authority)
                    return;
                ZRoutedRpc rpc = null;
                try { rpc = ZRoutedRpc.instance; } catch { rpc = null; }
                if (rpc == null)
                    return;
                long nonce = 0L;
                try { nonce = (long)(System.DateTime.UtcNow.Ticks); } catch { nonce = now.GetHashCode(); }
                ZPackage pkg = new ZPackage();
                try
                {
                    pkg.Write(KindPing);
                    pkg.Write(nonce);
                    pkg.Write(Plugin.PluginVersion != null ? Plugin.PluginVersion : "?");
                    pkg.Write(ServerAuthority.EffectiveMode().ToString());
                }
                catch
                {
                    return;
                }
                try { rpc.InvokeRoutedRPC(ServerRequestRpc, new object[1] { pkg }); } catch { }
            }
            catch
            {
            }
        }

        private static void OnServerRequest(long sender, ZPackage pkg)
        {
            try
            {
                if (!Plugin.IsActive)
                    return;
                ZNet net = null;
                try { net = ZNet.instance; } catch { net = null; }
                if ((UnityEngine.Object)net == (UnityEngine.Object)null)
                    return;
                bool isServer = false;
                try { isServer = net.IsServer(); } catch { isServer = false; }
                if (!isServer)
                    return;
                bool authority = false;
                try { authority = ServerAuthority.IsAuthorityMode(); } catch { authority = false; }
                if (!authority)
                    return;
                if (pkg == null)
                    return;
                byte kind = 255;
                try { kind = pkg.ReadByte(); } catch { return; }
                if (kind == KindTx)
                {
                    ZDOID chestId = ZDOID.None;
                    ZPackage inner = null;
                    try
                    {
                        chestId = pkg.ReadZDOID();
                        inner = pkg.ReadPackage();
                    }
                    catch { return; }
                    if (chestId.IsNone() || inner == null)
                        return;
                    long txId = 0L;
                    ZPackage payload = null;
                    try
                    {
                        txId = inner.ReadLong();
                        payload = inner.ReadPackage();
                    }
                    catch { return; }
                    if (payload == null)
                        return;
                    try { ServerChestManager.OnRequest(sender, chestId, txId, payload); } catch { }
                    return;
                }
                if (kind != KindPing)
                {
                    try
                    {
                        TxLog.Warn("server-director: unknown request kind=" + kind + " from=" + sender + " dropped (fail closed)");
                    }
                    catch { }
                    return;
                }
                long nonce = 0L;
                string version = "?";
                string mode = "?";
                try
                {
                    pkg.SetPos(0);
                    pkg.ReadByte();
                    nonce = pkg.ReadLong();
                    version = pkg.ReadString();
                    mode = pkg.ReadString();
                }
                catch { }
                ZPackage pong = new ZPackage();
                try
                {
                    pong.Write(KindPong);
                    pong.Write(nonce);
                    pong.Write(Plugin.PluginVersion != null ? Plugin.PluginVersion : "?");
                    pong.Write(ServerAuthority.EffectiveMode().ToString());
                }
                catch
                {
                    return;
                }
                try
                {
                    Plugin.LogInstance.LogInfo((object)("[ChestTX] server-director: ping from=" + sender + " version=" + version + " mode=" + mode));
                }
                catch { }
                ZRoutedRpc rpc = null;
                try { rpc = ZRoutedRpc.instance; } catch { rpc = null; }
                if (rpc == null)
                    return;
                try { rpc.InvokeRoutedRPC(sender, ServerResponseRpc, new object[1] { pong }); } catch { }
            }
            catch
            {
            }
        }

        private static void OnServerResponse(long sender, ZPackage pkg)
        {
            try
            {
                if (!Plugin.IsActive)
                    return;
                if (pkg == null)
                    return;
                byte kind = 255;
                try { kind = pkg.ReadByte(); } catch { return; }
                if (kind == KindTx)
                {
                    ZDOID chestId = ZDOID.None;
                    ZPackage inner = null;
                    try
                    {
                        chestId = pkg.ReadZDOID();
                        inner = pkg.ReadPackage();
                    }
                    catch { return; }
                    if (chestId.IsNone() || inner == null)
                        return;
                    try { ChestTxService.HandleServerTxResponse(sender, chestId, inner); } catch { }
                    return;
                }
                if (kind != KindPong)
                    return;
                long nonce = 0L;
                string version = "?";
                string mode = "?";
                try
                {
                    nonce = pkg.ReadLong();
                    version = pkg.ReadString();
                    mode = pkg.ReadString();
                }
                catch { }
                long rttMs = -1L;
                try { rttMs = (System.DateTime.UtcNow.Ticks - nonce) / 10000L; } catch { }
                bool first = false;
                try
                {
                    if (!_pongSeen)
                    {
                        _pongSeen = true;
                        first = true;
                    }
                }
                catch { }
                try
                {
                    if (first)
                        Plugin.LogInstance.LogInfo((object)("[ChestTX] server-director: pong from=" + sender + " rttMs=" + rttMs + " version=" + version + " mode=" + mode));
                    else
                        TxLog.Info("server-director: pong from=" + sender + " rttMs=" + rttMs);
                }
                catch { }
            }
            catch
            {
            }
        }
    }
}
