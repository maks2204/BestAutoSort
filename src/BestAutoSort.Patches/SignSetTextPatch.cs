using BestAutoSort.Core;
using BestAutoSort.Runtime;
using HarmonyLib;
using UnityEngine;

namespace BestAutoSort.Patches;

[HarmonyPatch(typeof(Sign), "SetText")]
internal static class SignSetTextPatch
{
	private const string AutomaticColorKey = "BestAutoSort.SignAutomaticColor";

	private static void Prefix(ref string text, out string __state)
	{
		text = SignTextColorMarkup.ApplyDefault(text, ModConfig.SignTextColor.Value, out __state);
	}

	private static void Postfix(string __state, ZNetView ___m_nview)
	{
		if (!((Object)(object)___m_nview == (Object)null) && ___m_nview.IsValid() && ___m_nview.IsOwner())
		{
			ZDO zDO = ___m_nview.GetZDO();
			if (zDO != null)
			{
				zDO.Set("BestAutoSort.SignAutomaticColor", __state);
			}
		}
	}
}
