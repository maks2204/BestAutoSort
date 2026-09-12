using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace BestAutoSort.Runtime;

internal static class ConfigFileMigration
{
	internal const string CurrentFileName = "dev.maks2204.bestautosort.cfg";

	internal static ConfigFile Open(BaseUnityPlugin plugin, string configDirectory, ManualLogSource log)
	{
		Directory.CreateDirectory(configDirectory);
		return new ConfigFile(Path.Combine(configDirectory, CurrentFileName), true, plugin.Info.Metadata);
	}
}
