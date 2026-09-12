using UnityEngine;

namespace BestAutoSort.Runtime;

internal sealed class AutoFeedContainerTracker : MonoBehaviour
{
	internal Container Container;

	private void OnDestroy()
	{
		if (Container != null)
		{
			AutoFeedService.ForgetContainer(Container);
		}
	}
}
