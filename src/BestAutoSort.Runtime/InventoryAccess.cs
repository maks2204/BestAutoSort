using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace BestAutoSort.Runtime;

internal static class InventoryAccess
{
	private static readonly FieldInfo CurrentContainerField = AccessTools.Field(typeof(InventoryGui), "m_currentContainer");

	internal static Container? CurrentContainer(InventoryGui? gui)
	{
		if (!((Object)(object)gui == (Object)null))
		{
			object value = CurrentContainerField.GetValue(gui);
			return (Container?)((value is Container) ? value : null);
		}
		return null;
	}
}
