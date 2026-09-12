using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace BestAutoSort.Runtime;

internal sealed class ChestUpgradeVisualState : MonoBehaviour
{
	[CompilerGenerated]
	private Bounds _003CBaseClosedBounds_003Ek__BackingField;

	internal int AppliedTier { get; set; }

	internal bool HasBaseBounds { get; set; }

	internal Bounds BaseClosedBounds
	{
		[CompilerGenerated]
		get
		{
			return _003CBaseClosedBounds_003Ek__BackingField;
		}
		[CompilerGenerated]
		set
		{
			_003CBaseClosedBounds_003Ek__BackingField = value;
		}
	}

	internal GameObject? OriginalClosed { get; set; }

	internal GameObject? OriginalOpen { get; set; }

	internal GameObject? OriginalVisualRoot { get; set; }

	internal Renderer[] OriginalRenderers { get; set; } = Array.Empty<Renderer>();

	internal Collider[] OriginalColliders { get; set; } = Array.Empty<Collider>();

	internal bool[] OriginalColliderEnabled { get; set; } = Array.Empty<bool>();

	internal GameObject? VisualRoot { get; set; }

	internal GameObject? CollisionRoot { get; set; }
}
