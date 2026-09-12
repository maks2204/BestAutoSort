namespace BestAutoSort.Core;

public sealed class ItemClassificationInput
{
	public string PrefabName { get; set; } = string.Empty;

	public string SharedName { get; set; } = string.Empty;

	public string ItemType { get; set; } = string.Empty;

	public string AttachOverrideType { get; set; } = string.Empty;

	public string SkillType { get; set; } = string.Empty;

	public string AmmoType { get; set; } = string.Empty;

	public bool IsQuestItem { get; set; }

	public bool HasBuildPieces { get; set; }

	public bool HasConsumeStatusEffect { get; set; }

	public int Value { get; set; }

	public float FoodHealth { get; set; }

	public float FoodStamina { get; set; }

	public float FoodEitr { get; set; }
}
