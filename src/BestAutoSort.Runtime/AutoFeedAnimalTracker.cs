using UnityEngine;

namespace BestAutoSort.Runtime;

internal sealed class AutoFeedAnimalTracker : MonoBehaviour
{
	internal Tameable Animal;

	private void OnDestroy()
	{
		if (Animal != null)
		{
			AutoFeedService.ForgetAnimal(Animal);
		}
	}
}
