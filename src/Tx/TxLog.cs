using BestAutoSort.Runtime;

namespace BestAutoSort.Tx
{
    /// <summary>
    /// ChestTX logging. Format:
    /// [ChestTX] container=&lt;ZDOID&gt; tx=&lt;id&gt; peer=&lt;id&gt; op=ADD item=Wood requested=50 accepted=50 revision=123-&gt;124
    /// Enabled via the Debug.TxVerbose option. Without it — warnings/errors only.
    /// Never logs per frame: transaction events only.
    /// </summary>
    internal static class TxLog
    {
        private const string Tag = "[ChestTX]";

        public static void Info(string message)
        {
            if (ModConfig.TxVerbose.Value)
                Plugin.LogInstance.LogInfo(Tag + " " + message);
        }

        public static void Warn(string message)
        {
            Plugin.LogInstance.LogWarning(Tag + " " + message);
        }

        public static void Error(string message)
        {
            Plugin.LogInstance.LogError(Tag + " " + message);
        }

        public static string Zid(ZDOID id)
        {
            return id.ToString();
        }
    }
}
