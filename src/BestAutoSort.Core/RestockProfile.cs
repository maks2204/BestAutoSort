namespace BestAutoSort.Core;

internal sealed class RestockProfile
{
	internal string Id { get; set; }

	internal RestockInventoryKind InventoryKind { get; set; }

	internal int X { get; set; }

	internal int Y { get; set; }

	internal string PrefabName { get; set; }

	internal int Quality { get; set; }

	internal int Variant { get; set; }

	internal int Target { get; set; }

	internal RestockProfile(string id, RestockInventoryKind inventoryKind, int x, int y, string prefabName, int quality, int variant, int target)
	{
		Id = id;
		InventoryKind = inventoryKind;
		X = x;
		Y = y;
		PrefabName = prefabName;
		Quality = quality;
		Variant = variant;
		Target = target;
	}
}
