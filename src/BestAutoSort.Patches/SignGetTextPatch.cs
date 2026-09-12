using BestAutoSort.Core;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace BestAutoSort.Patches;

[HarmonyPatch(typeof(Sign), "GetText")]
internal static class SignGetTextPatch
{
	private const string AutomaticColorKey = "BestAutoSort.SignAutomaticColor";

	private static bool Prefix(Sign __instance, ref string __result, ZNetView ___m_nview)
	{
		ZDO obj = (((Object)(object)___m_nview != (Object)null && ___m_nview.IsValid()) ? ___m_nview.GetZDO() : null);
		string text = ((obj != null) ? obj.GetString("BestAutoSort.SignAutomaticColor", string.Empty) : null) ?? string.Empty;
		string text2 = (((Object)(object)__instance.m_textWidget != (Object)null) ? ((TMP_Text)__instance.m_textWidget).text : string.Empty);
		__result = ((text.Length > 0) ? SignTextColorMarkup.RemoveAutomatic(text2, text) : SignTextColorMarkup.RemoveLegacyYellow(text2));
		return false;
	}
}
