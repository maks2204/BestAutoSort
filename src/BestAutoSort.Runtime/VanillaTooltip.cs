using System.Linq;
using UnityEngine;

namespace BestAutoSort.Runtime;

internal static class VanillaTooltip
{
	internal static UITooltip? Attach(GameObject target, Component templateRoot, string topic, string text)
	{
		UITooltip tooltip = target.GetComponent<UITooltip>();
		if ((Object)(object)tooltip == (Object)null || (Object)(object)tooltip.m_tooltipPrefab == (Object)null)
		{
			UITooltip val = templateRoot.GetComponentsInChildren<UITooltip>(true).FirstOrDefault((UITooltip candidate) => (Object)(object)candidate != (Object)null && (Object)(object)candidate != (Object)(object)tooltip && (Object)(object)candidate.m_tooltipPrefab != (Object)null);
			if ((Object)(object)val == (Object)null)
			{
				if ((Object)(object)tooltip != (Object)null)
				{
					((Behaviour)tooltip).enabled = false;
				}
				return null;
			}
			if (tooltip == null)
			{
				tooltip = target.AddComponent<UITooltip>();
			}
			tooltip.m_tooltipPrefab = val.m_tooltipPrefab;
		}
		((Behaviour)tooltip).enabled = true;
		tooltip.m_topic = topic;
		tooltip.m_text = text;
		return tooltip;
	}
}
