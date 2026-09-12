namespace BestAutoSort.Core;

public sealed class StoragePreset
{
	public string Rule { get; }

	public string Reserves { get; }

	public StoragePreset(string rule, string reserves)
	{
		Rule = rule;
		Reserves = reserves;
	}
}
