using System;
using System.Collections.Generic;

namespace BestAutoSort.Core;

public static class ItemCategoryCatalog
{
	private static readonly ItemCategoryGroupDefinition[] GroupDefinitions = new ItemCategoryGroupDefinition[11]
	{
		new ItemCategoryGroupDefinition("weapons", "Weapons"),
		new ItemCategoryGroupDefinition("ammunition", "Ammunition"),
		new ItemCategoryGroupDefinition("armor", "Armor & Equipment"),
		new ItemCategoryGroupDefinition("tools", "Tools"),
		new ItemCategoryGroupDefinition("food", "Food & Consumables"),
		new ItemCategoryGroupDefinition("cooking", "Cooking Ingredients"),
		new ItemCategoryGroupDefinition("materials", "Materials"),
		new ItemCategoryGroupDefinition("farming", "Farming"),
		new ItemCategoryGroupDefinition("collectibles", "Collectibles"),
		new ItemCategoryGroupDefinition("progression", "Progression"),
		new ItemCategoryGroupDefinition("misc", "Miscellaneous")
	};

	private static readonly ItemCategoryDefinition[] Definitions = new ItemCategoryDefinition[75]
	{
		Define(ItemCategory.Sword, "weapons.swords", "Weapons", "Swords"),
		Define(ItemCategory.Knife, "weapons.knives", "Weapons", "Knives"),
		Define(ItemCategory.Club, "weapons.clubs", "Weapons", "Clubs & Maces"),
		Define(ItemCategory.Axe, "weapons.axes", "Weapons", "Axes"),
		Define(ItemCategory.Spear, "weapons.spears", "Weapons", "Spears"),
		Define(ItemCategory.Polearm, "weapons.polearms", "Weapons", "Polearms"),
		Define(ItemCategory.FistWeapon, "weapons.fists", "Weapons", "Fist Weapons"),
		Define(ItemCategory.Bow, "weapons.bows", "Weapons", "Bows"),
		Define(ItemCategory.Crossbow, "weapons.crossbows", "Weapons", "Crossbows"),
		Define(ItemCategory.ElementalMagic, "weapons.elemental_magic", "Weapons", "Elemental Magic"),
		Define(ItemCategory.BloodMagic, "weapons.blood_magic", "Weapons", "Blood Magic"),
		Define(ItemCategory.Bomb, "weapons.bombs", "Weapons", "Bombs"),
		Define(ItemCategory.OtherWeapon, "weapons.other", "Weapons", "Other Weapons"),
		Define(ItemCategory.Arrow, "ammunition.arrows", "Ammunition", "Arrows"),
		Define(ItemCategory.Bolt, "ammunition.bolts", "Ammunition", "Bolts"),
		Define(ItemCategory.SiegeAmmunition, "ammunition.siege", "Ammunition", "Siege Ammunition"),
		Define(ItemCategory.OtherAmmunition, "ammunition.other", "Ammunition", "Other Ammunition"),
		Define(ItemCategory.FishingBait, "ammunition.fishing_bait", "Ammunition", "Fishing Bait"),
		Define(ItemCategory.Shield, "armor.shields", "Armor & Equipment", "Shields"),
		Define(ItemCategory.Helmet, "armor.helmets", "Armor & Equipment", "Helmets"),
		Define(ItemCategory.ChestArmor, "armor.chest", "Armor & Equipment", "Chest Armor"),
		Define(ItemCategory.LegArmor, "armor.legs", "Armor & Equipment", "Leg Armor"),
		Define(ItemCategory.HandArmor, "armor.hands", "Armor & Equipment", "Hand Armor"),
		Define(ItemCategory.Cape, "armor.capes", "Armor & Equipment", "Capes"),
		Define(ItemCategory.UtilityEquipment, "equipment.utility", "Armor & Equipment", "Utility Equipment"),
		Define(ItemCategory.Trinket, "equipment.trinkets", "Armor & Equipment", "Trinkets"),
		Define(ItemCategory.Pickaxe, "tools.pickaxes", "Tools", "Pickaxes"),
		Define(ItemCategory.BuildingTool, "tools.building", "Tools", "Building Tools"),
		Define(ItemCategory.FarmingTool, "tools.farming", "Tools", "Farming Tools"),
		Define(ItemCategory.FishingTool, "tools.fishing", "Tools", "Fishing Tools"),
		Define(ItemCategory.Torch, "tools.lights", "Tools", "Portable Lights"),
		Define(ItemCategory.Saddle, "tools.saddles", "Tools", "Saddles"),
		Define(ItemCategory.OtherTool, "tools.other", "Tools", "Other Tools"),
		Define(ItemCategory.HealthFood, "food.health", "Food & Consumables", "Health Food"),
		Define(ItemCategory.StaminaFood, "food.stamina", "Food & Consumables", "Stamina Food"),
		Define(ItemCategory.EitrFood, "food.eitr", "Food & Consumables", "Eitr Food"),
		Define(ItemCategory.BalancedFood, "food.balanced", "Food & Consumables", "Balanced Food"),
		Define(ItemCategory.Feast, "food.feasts", "Food & Consumables", "Feasts"),
		Define(ItemCategory.Mead, "consumables.meads", "Food & Consumables", "Meads"),
		Define(ItemCategory.Potion, "consumables.potions", "Food & Consumables", "Potions"),
		Define(ItemCategory.OtherConsumable, "consumables.other", "Food & Consumables", "Other Consumables"),
		Define(ItemCategory.MeadBase, "cooking.mead_bases", "Cooking Ingredients", "Mead Bases"),
		Define(ItemCategory.RawMeat, "cooking.raw_meat", "Cooking Ingredients", "Raw Meat"),
		Define(ItemCategory.RawFish, "cooking.raw_fish", "Cooking Ingredients", "Raw Fish"),
		Define(ItemCategory.Produce, "cooking.produce", "Cooking Ingredients", "Produce & Berries"),
		Define(ItemCategory.Mushroom, "cooking.mushrooms", "Cooking Ingredients", "Mushrooms"),
		Define(ItemCategory.UncookedFood, "cooking.uncooked", "Cooking Ingredients", "Uncooked Dishes"),
		Define(ItemCategory.Spice, "cooking.spices", "Cooking Ingredients", "Spices"),
		Define(ItemCategory.OtherCookingIngredient, "cooking.other", "Cooking Ingredients", "Other Cooking Ingredients"),
		Define(ItemCategory.Wood, "materials.wood", "Materials", "Wood"),
		Define(ItemCategory.Stone, "materials.stone", "Materials", "Stone"),
		Define(ItemCategory.Ore, "materials.ore", "Materials", "Ores & Scrap"),
		Define(ItemCategory.Metal, "materials.metal", "Materials", "Metals"),
		Define(ItemCategory.HideAndLeather, "materials.hides", "Materials", "Hides & Leather"),
		Define(ItemCategory.BoneAndTeeth, "materials.bones", "Materials", "Bones, Teeth & Claws"),
		Define(ItemCategory.ShellAndScale, "materials.shells", "Materials", "Shells & Scales"),
		Define(ItemCategory.Textile, "materials.textiles", "Materials", "Textiles & Fibres"),
		Define(ItemCategory.Gem, "materials.gems", "Materials", "Gems & Crystal"),
		Define(ItemCategory.MechanicalComponent, "materials.mechanical", "Materials", "Mechanical Components"),
		Define(ItemCategory.MagicMaterial, "materials.magic", "Materials", "Magic Materials"),
		Define(ItemCategory.PlantMaterial, "materials.plants", "Materials", "Plant Materials"),
		Define(ItemCategory.CreatureDrop, "materials.creature_drops", "Materials", "Creature Drops"),
		Define(ItemCategory.Fuel, "materials.fuel", "Materials", "Fuel & Resin"),
		Define(ItemCategory.OtherMaterial, "materials.other", "Materials", "Other Materials"),
		Define(ItemCategory.Seed, "farming.seeds", "Farming", "Seeds & Cones"),
		Define(ItemCategory.Egg, "farming.eggs", "Farming", "Eggs"),
		Define(ItemCategory.Trophy, "collectibles.trophies", "Collectibles", "Trophies"),
		Define(ItemCategory.Valuable, "collectibles.valuables", "Collectibles", "Valuables"),
		Define(ItemCategory.Firework, "collectibles.fireworks", "Collectibles", "Fireworks"),
		Define(ItemCategory.BossSummoningItem, "progression.boss_summoning", "Progression", "Boss Summoning Items"),
		Define(ItemCategory.BossDrop, "progression.boss_drops", "Progression", "Boss Drops"),
		Define(ItemCategory.Key, "progression.keys", "Progression", "Keys & Sealbreakers"),
		Define(ItemCategory.QuestItem, "progression.quest", "Progression", "Quest Items"),
		Define(ItemCategory.Customization, "misc.customization", "Miscellaneous", "Customization"),
		Define(ItemCategory.Miscellaneous, "misc.other", "Miscellaneous", "Miscellaneous")
	};

	private static readonly Dictionary<ItemCategory, ItemCategoryDefinition> ByCategory = BuildByCategory();

	private static readonly Dictionary<string, ItemCategoryDefinition> ById = BuildById();

	private static readonly Dictionary<string, ItemCategoryGroupDefinition> GroupsById = BuildGroupsById();

	public static IReadOnlyList<ItemCategoryDefinition> All => Definitions;

	public static IReadOnlyList<ItemCategoryGroupDefinition> Groups => GroupDefinitions;

	public static ItemCategoryDefinition Get(ItemCategory category)
	{
		if (!ByCategory.TryGetValue(category, out ItemCategoryDefinition value) || value == null)
		{
			throw new ArgumentOutOfRangeException("category", category, "Unknown item category.");
		}
		return value;
	}

	public static bool TryGet(string id, out ItemCategoryDefinition? definition)
	{
		return ById.TryGetValue(id ?? string.Empty, out definition);
	}

	public static bool TryGetGroup(string id, out ItemCategoryGroupDefinition? definition)
	{
		return GroupsById.TryGetValue(id ?? string.Empty, out definition);
	}

	public static int SortRank(ItemCategory category)
	{
		for (int i = 0; i < Definitions.Length; i++)
		{
			if (Definitions[i].Category == category)
			{
				return i;
			}
		}
		return Definitions.Length;
	}

	private static ItemCategoryDefinition Define(ItemCategory category, string id, string group, string displayName)
	{
		return new ItemCategoryDefinition(category, id, GroupId(group), group, displayName);
	}

	private static string GroupId(string displayName)
	{
		for (int i = 0; i < GroupDefinitions.Length; i++)
		{
			if (string.Equals(GroupDefinitions[i].DisplayName, displayName, StringComparison.Ordinal))
			{
				return GroupDefinitions[i].Id;
			}
		}
		throw new InvalidOperationException("Category references an unknown main group: " + displayName);
	}

	private static Dictionary<ItemCategory, ItemCategoryDefinition> BuildByCategory()
	{
		Dictionary<ItemCategory, ItemCategoryDefinition> dictionary = new Dictionary<ItemCategory, ItemCategoryDefinition>();
		ItemCategoryDefinition[] definitions = Definitions;
		foreach (ItemCategoryDefinition itemCategoryDefinition in definitions)
		{
			dictionary.Add(itemCategoryDefinition.Category, itemCategoryDefinition);
		}
		return dictionary;
	}

	private static Dictionary<string, ItemCategoryDefinition> BuildById()
	{
		Dictionary<string, ItemCategoryDefinition> dictionary = new Dictionary<string, ItemCategoryDefinition>(StringComparer.OrdinalIgnoreCase);
		ItemCategoryDefinition[] definitions = Definitions;
		foreach (ItemCategoryDefinition itemCategoryDefinition in definitions)
		{
			dictionary.Add(itemCategoryDefinition.Id, itemCategoryDefinition);
		}
		return dictionary;
	}

	private static Dictionary<string, ItemCategoryGroupDefinition> BuildGroupsById()
	{
		Dictionary<string, ItemCategoryGroupDefinition> dictionary = new Dictionary<string, ItemCategoryGroupDefinition>(StringComparer.OrdinalIgnoreCase);
		ItemCategoryGroupDefinition[] groupDefinitions = GroupDefinitions;
		foreach (ItemCategoryGroupDefinition itemCategoryGroupDefinition in groupDefinitions)
		{
			dictionary.Add(itemCategoryGroupDefinition.Id, itemCategoryGroupDefinition);
		}
		return dictionary;
	}
}
