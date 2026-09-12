using System;
using System.Collections.Generic;

namespace BestAutoSort.Core;

public static class ItemCategoryClassifier
{
	private static readonly HashSet<string> BossSummoningItems = Names("AncientSeed", "WitheredBone", "DragonEgg", "GoblinTotem");

	private static readonly HashSet<string> BossDrops = Names("HardAntler", "DragonTear", "YagluthDrop", "QueenDrop", "FaderDrop");

	private static readonly HashSet<string> Keys = Names("CryptKey", "DvergrKey", "DvergrKeyFragment", "HildirKey_forestcrypt", "HildirKey_mountaincave", "HildirKey_plainsfortress");

	private static readonly HashSet<string> QuestItems = Names("Bell", "BellFragment", "chest_hildir1", "chest_hildir2", "chest_hildir3", "DyrnwynBladeFragment", "DyrnwynHiltFragment", "DyrnwynTipFragment");

	private static readonly HashSet<string> Valuables = Names("Amber", "AmberPearl", "Coins", "Ruby", "SilverNecklace");

	private static readonly HashSet<string> Seeds = Names("Acorn", "BeechSeeds", "BirchSeeds", "CarrotSeeds", "FirCone", "OnionSeeds", "PineCone", "TurnipSeeds", "VineberrySeeds", "VineGreenSeeds");

	private static readonly HashSet<string> Eggs = Names("AsksvinEgg", "ChickenEgg", "VoltureEgg");

	private static readonly HashSet<string> Saddles = Names("SaddleAsksvin", "SaddleLox");

	private static readonly HashSet<string> BuildingTools = Names("Feaster", "Hammer");

	private static readonly HashSet<string> FarmingTools = Names("Cultivator", "Hoe", "KnifeButcher", "Scythe");

	private static readonly HashSet<string> OtherTools = Names("BarberKit", "Tankard", "Tankard_dvergr", "TankardAnniversary", "TankardOdin");

	private static readonly HashSet<string> OtherConsumables = Names("HealthUpgrade_Bonemass", "HealthUpgrade_GDKing", "Pukeberries", "StaminaUpgrade_Greydwarf", "StaminaUpgrade_Troll", "StaminaUpgrade_Wraith");

	private static readonly HashSet<string> SiegeAmmunition = Names("BombSiege", "Catapult_ammo", "TurretBolt", "TurretBoltBone", "TurretBoltFlametal", "TurretBoltWood");

	private static readonly HashSet<string> Produce = Names("Barley", "Blueberries", "Carrot", "Cloudberry", "Fiddleheadfern", "Honey", "Onion", "Raspberry", "Turnip", "Vineberry");

	private static readonly HashSet<string> Mushrooms = Names("Mushroom", "MushroomBlue", "MushroomBzerker", "MushroomJotunPuffs", "MushroomMagecap", "MushroomSmokePuff", "MushroomYellow");

	private static readonly HashSet<string> RawMeats = Names("AsksvinMeat", "BjornMeat", "BoneMawSerpentMeat", "BugMeat", "ChickenMeat", "DeerMeat", "HareMeat", "LoxMeat", "NeckTail", "RawMeat", "RottenMeat", "SerpentMeat", "VoltureMeat", "WolfMeat");

	private static readonly HashSet<string> RawFish = Names("FishAnglerRaw", "FishRaw");

	private static readonly HashSet<string> UncookedFoods = Names("BreadDough", "FishAndBreadUncooked", "HoneyGlazedChickenUncooked", "LoxPieUncooked", "MagicallyStuffedShroomUncooked", "MeatPlatterUncooked", "MisthareSupremeUncooked", "PiquantPieUncooked", "RoastedCrustPieUncooked", "VikingCupcakeUncooked");

	private static readonly HashSet<string> OtherCookingIngredients = Names("BarleyFlour", "Bloodbag", "CuredSquirrelHamstring", "Entrails", "FragrantBundle", "FreezeGland", "FreshSeaweed", "GiantBloodSack", "GreydwarfEye", "Ooze", "PowderedDragonEgg", "PungentPebbles", "RoyalJelly", "Sap");

	private static readonly HashSet<string> Woods = Names("Blackwood", "ElderBark", "FineWood", "RoundLog", "Wood", "YggdrasilWood");

	private static readonly HashSet<string> Stones = Names("BlackMarble", "Flint", "Grausten", "Obsidian", "Stone", "StoneRock");

	private static readonly HashSet<string> Ores = Names("BlackMetalScrap", "BronzeScrap", "CopperOre", "CopperScrap", "FlametalOre", "FlametalOreNew", "IronOre", "IronScrap", "SilverOre", "TinOre");

	private static readonly HashSet<string> Metals = Names("BlackMetal", "Bronze", "Copper", "Flametal", "FlametalNew", "Iron", "Silver", "Tin");

	private static readonly HashSet<string> Hides = Names("AskHide", "BjornHide", "DeerHide", "LeatherScraps", "LoxPelt", "ScaleHide", "TrollHide", "WolfPelt");

	private static readonly HashSet<string> Bones = Names("AsksvinCarrionNeck", "AsksvinCarrionPelvic", "AsksvinCarrionRibcage", "AsksvinCarrionSkull", "BoneFragments", "BonemawSerpentTooth", "CharredBone", "Charredskull", "UndeadBjornRibcage", "WolfClaw", "WolfFang");

	private static readonly HashSet<string> Shells = Names("BonemawSerpentScale", "Carapace", "Chitin", "Mandible", "SerpentScale");

	private static readonly HashSet<string> Textiles = Names("CandleWick", "Flax", "JuteBlue", "JuteRed", "LinenThread", "MorgenSinew", "WolfHairBundle");

	private static readonly HashSet<string> Gems = Names("Crystal", "GemstoneBlue", "GemstoneGreen", "GemstoneRed");

	private static readonly HashSet<string> MechanicalComponents = Names("AxeHead1", "AxeHead2", "BarrelRings", "BronzeNails", "CeramicPlate", "Chain", "CharredCogwheel", "DvergrNeedle", "IronNails", "Ironpit", "MechanicalSpring", "ScytheHandle", "SharpeningStone");

	private static readonly HashSet<string> MagicMaterials = Names("BlackCore", "CelestialFeather", "Ectoplasm", "Eitr", "MoltenCore", "ShieldCore", "Softtissue", "SurtlingCore", "Thunderstone", "Wisp", "YmirRemains");

	private static readonly HashSet<string> PlantMaterials = Names("Dandelion", "Guck", "Root", "Thistle");

	private static readonly HashSet<string> CreatureDrops = Names("AskBladder", "Bilebag", "BjornPaw", "BlobVial", "Feathers", "Larva", "MorgenHeart", "Needle", "ProustitePowder", "QueenBee", "SulfurStone");

	private static readonly HashSet<string> Fuels = Names("CharcoalResin", "Coal", "Resin", "Tar");

	public static ItemCategory Classify(ItemClassificationInput item)
	{
		if (item == null)
		{
			throw new ArgumentNullException("item");
		}
		if (item.IsQuestItem)
		{
			return ItemCategory.QuestItem;
		}
		if (IdentityMatches(BossSummoningItems, item))
		{
			return ItemCategory.BossSummoningItem;
		}
		if (IdentityMatches(BossDrops, item))
		{
			return ItemCategory.BossDrop;
		}
		if (IdentityMatches(Keys, item))
		{
			return ItemCategory.Key;
		}
		if (IdentityMatches(QuestItems, item))
		{
			return ItemCategory.QuestItem;
		}
		if (IdentityMatches(Valuables, item))
		{
			return ItemCategory.Valuable;
		}
		if (IdentityStartsWith(item, "FireworksRocket"))
		{
			return ItemCategory.Firework;
		}
		if (IdentityMatches(SiegeAmmunition, item))
		{
			return ItemCategory.SiegeAmmunition;
		}
		if (IdentityMatches(Saddles, item))
		{
			return ItemCategory.Saddle;
		}
		if (IdentityMatches(Seeds, item))
		{
			return ItemCategory.Seed;
		}
		if (IdentityMatches(Eggs, item))
		{
			return ItemCategory.Egg;
		}
		if (IdentityStartsWith(item, "Feast"))
		{
			return ItemCategory.Feast;
		}
		if (IsMeadBase(item))
		{
			return ItemCategory.MeadBase;
		}
		if (IdentityMatches(UncookedFoods, item))
		{
			return ItemCategory.UncookedFood;
		}
		if (IdentityMatches(RawMeats, item))
		{
			return ItemCategory.RawMeat;
		}
		if (IdentityMatches(RawFish, item) || IsType(item, "Fish"))
		{
			return ItemCategory.RawFish;
		}
		if (IdentityMatches(Mushrooms, item))
		{
			return ItemCategory.Mushroom;
		}
		if (IdentityMatches(Produce, item))
		{
			return ItemCategory.Produce;
		}
		if (IdentityStartsWith(item, "Spice"))
		{
			return ItemCategory.Spice;
		}
		if (IdentityMatches(OtherCookingIngredients, item))
		{
			return ItemCategory.OtherCookingIngredient;
		}
		if (IsType(item, "Shield"))
		{
			return ItemCategory.Shield;
		}
		if (IsType(item, "Helmet"))
		{
			return ItemCategory.Helmet;
		}
		if (IsType(item, "Chest"))
		{
			return ItemCategory.ChestArmor;
		}
		if (IsType(item, "Legs"))
		{
			return ItemCategory.LegArmor;
		}
		if (IsType(item, "Hands"))
		{
			return ItemCategory.HandArmor;
		}
		if (IsType(item, "Shoulder"))
		{
			return ItemCategory.Cape;
		}
		if (IsType(item, "Utility"))
		{
			return ItemCategory.UtilityEquipment;
		}
		if (IsType(item, "Trinket"))
		{
			return ItemCategory.Trinket;
		}
		if (IsFishingBait(item))
		{
			return ItemCategory.FishingBait;
		}
		if (IsArrow(item))
		{
			return ItemCategory.Arrow;
		}
		if (IsBolt(item))
		{
			return ItemCategory.Bolt;
		}
		if (IsType(item, "Ammo") || IsType(item, "AmmoNonEquipable"))
		{
			return ItemCategory.OtherAmmunition;
		}
		if (IsSkill(item, "Pickaxes"))
		{
			return ItemCategory.Pickaxe;
		}
		if (item.HasBuildPieces || IdentityMatches(BuildingTools, item))
		{
			return ItemCategory.BuildingTool;
		}
		if (IsSkill(item, "Farming") || IdentityMatches(FarmingTools, item))
		{
			return ItemCategory.FarmingTool;
		}
		if (IsSkill(item, "Fishing") || IdentityStartsWith(item, "FishingRod"))
		{
			return ItemCategory.FishingTool;
		}
		if (IsType(item, "Torch"))
		{
			return ItemCategory.Torch;
		}
		if (IdentityMatches(OtherTools, item))
		{
			return ItemCategory.OtherTool;
		}
		if (IsBomb(item))
		{
			return ItemCategory.Bomb;
		}
		if (IsWeaponType(item))
		{
			if (IsSkill(item, "Swords"))
			{
				return ItemCategory.Sword;
			}
			if (IsSkill(item, "Knives"))
			{
				return ItemCategory.Knife;
			}
			if (IsSkill(item, "Clubs"))
			{
				return ItemCategory.Club;
			}
			if (IsSkill(item, "Axes") || IsSkill(item, "WoodCutting"))
			{
				return ItemCategory.Axe;
			}
			if (IsSkill(item, "Spears"))
			{
				return ItemCategory.Spear;
			}
			if (IsSkill(item, "Polearms") || IsType(item, "Attach_Atgeir"))
			{
				return ItemCategory.Polearm;
			}
			if (IsSkill(item, "Unarmed"))
			{
				return ItemCategory.FistWeapon;
			}
			if (IsSkill(item, "ElementalMagic"))
			{
				return ItemCategory.ElementalMagic;
			}
			if (IsSkill(item, "BloodMagic"))
			{
				return ItemCategory.BloodMagic;
			}
			if (IsSkill(item, "Crossbows"))
			{
				return ItemCategory.Crossbow;
			}
			if (IsSkill(item, "Bows") || IsType(item, "Bow"))
			{
				return ItemCategory.Bow;
			}
			return ItemCategory.OtherWeapon;
		}
		if (IsType(item, "Tool"))
		{
			return ItemCategory.OtherTool;
		}
		if (IdentityMatches(OtherConsumables, item))
		{
			return ItemCategory.OtherConsumable;
		}
		if (HasFoodStats(item))
		{
			return ClassifyFood(item);
		}
		if (IsMead(item))
		{
			return ItemCategory.Mead;
		}
		if (item.HasConsumeStatusEffect)
		{
			return ItemCategory.Potion;
		}
		if (IsType(item, "Consumable"))
		{
			return ItemCategory.OtherConsumable;
		}
		if (IsType(item, "Trophy"))
		{
			return ItemCategory.Trophy;
		}
		if (IdentityMatches(Woods, item))
		{
			return ItemCategory.Wood;
		}
		if (IdentityMatches(Stones, item))
		{
			return ItemCategory.Stone;
		}
		if (IdentityMatches(Ores, item))
		{
			return ItemCategory.Ore;
		}
		if (IdentityMatches(Metals, item))
		{
			return ItemCategory.Metal;
		}
		if (IdentityMatches(Hides, item) || IdentityEndsWith(item, "Hide") || IdentityEndsWith(item, "Pelt"))
		{
			return ItemCategory.HideAndLeather;
		}
		if (IdentityMatches(Bones, item))
		{
			return ItemCategory.BoneAndTeeth;
		}
		if (IdentityMatches(Shells, item))
		{
			return ItemCategory.ShellAndScale;
		}
		if (IdentityMatches(Textiles, item))
		{
			return ItemCategory.Textile;
		}
		if (IdentityMatches(Gems, item))
		{
			return ItemCategory.Gem;
		}
		if (IdentityMatches(MechanicalComponents, item))
		{
			return ItemCategory.MechanicalComponent;
		}
		if (IdentityMatches(MagicMaterials, item))
		{
			return ItemCategory.MagicMaterial;
		}
		if (IdentityMatches(PlantMaterials, item))
		{
			return ItemCategory.PlantMaterial;
		}
		if (IdentityMatches(CreatureDrops, item))
		{
			return ItemCategory.CreatureDrop;
		}
		if (IdentityMatches(Fuels, item))
		{
			return ItemCategory.Fuel;
		}
		if (item.Value > 0 && (IsType(item, "Material") || IsType(item, "Misc")))
		{
			return ItemCategory.Valuable;
		}
		if (IsType(item, "Material"))
		{
			return ItemCategory.OtherMaterial;
		}
		if (IsType(item, "Customization"))
		{
			return ItemCategory.Customization;
		}
		return ItemCategory.Miscellaneous;
	}

	private static ItemCategory ClassifyFood(ItemClassificationInput item)
	{
		if (item.FoodEitr > 0f)
		{
			return ItemCategory.EitrFood;
		}
		float num = Math.Max(item.FoodHealth, item.FoodStamina);
		if (num > 0f && Math.Abs(item.FoodHealth - item.FoodStamina) <= num * 0.15f)
		{
			return ItemCategory.BalancedFood;
		}
		if (!(item.FoodHealth > item.FoodStamina))
		{
			return ItemCategory.StaminaFood;
		}
		return ItemCategory.HealthFood;
	}

	private static bool HasFoodStats(ItemClassificationInput item)
	{
		if (!(item.FoodHealth > 0f) && !(item.FoodStamina > 0f))
		{
			return item.FoodEitr > 0f;
		}
		return true;
	}

	private static bool IsWeaponType(ItemClassificationInput item)
	{
		if (!IsType(item, "OneHandedWeapon") && !IsType(item, "TwoHandedWeapon") && !IsType(item, "TwoHandedWeaponLeft") && !IsType(item, "Bow"))
		{
			return IsType(item, "Attach_Atgeir");
		}
		return true;
	}

	private static bool IsBomb(ItemClassificationInput item)
	{
		if (!IdentityStartsWith(item, "Bomb") && !IdentityStartsWith(item, "OozeBomb") && !IdentityStartsWith(item, "BileBomb"))
		{
			return IdentityStartsWith(item, "SmokeBomb");
		}
		return true;
	}

	private static bool IsMeadBase(ItemClassificationInput item)
	{
		if (!IdentityStartsWith(item, "MeadBase"))
		{
			return IdentityEndsWith(item, "WineBase");
		}
		return true;
	}

	private static bool IsMead(ItemClassificationInput item)
	{
		if (!IsMeadBase(item))
		{
			if (!IdentityStartsWith(item, "Mead"))
			{
				return IdentityEndsWith(item, "Wine");
			}
			return true;
		}
		return false;
	}

	private static bool IsFishingBait(ItemClassificationInput item)
	{
		if (!IdentityStartsWith(item, "FishingBait"))
		{
			return Contains(item.AmmoType, "bait");
		}
		return true;
	}

	private static bool IsArrow(ItemClassificationInput item)
	{
		if (!IdentityStartsWith(item, "Arrow"))
		{
			return Contains(item.AmmoType, "arrow");
		}
		return true;
	}

	private static bool IsBolt(ItemClassificationInput item)
	{
		if (!IdentityStartsWith(item, "Bolt"))
		{
			return Contains(item.AmmoType, "bolt");
		}
		return true;
	}

	private static bool IsType(ItemClassificationInput item, string value)
	{
		return EqualsIgnoreCase(item.ItemType, value);
	}

	private static bool IsSkill(ItemClassificationInput item, string value)
	{
		return EqualsIgnoreCase(item.SkillType, value);
	}

	private static bool IdentityMatches(ISet<string> names, ItemClassificationInput item)
	{
		string item2 = CleanName(item.PrefabName);
		string text = CleanName(item.SharedName);
		string item3 = CleanLocalizationToken(text);
		if (!names.Contains(item2) && !names.Contains(text))
		{
			return names.Contains(item3);
		}
		return true;
	}

	private static bool IdentityStartsWith(ItemClassificationInput item, string prefix)
	{
		if (!StartsWith(CleanName(item.PrefabName), prefix))
		{
			return StartsWith(CleanLocalizationToken(item.SharedName), prefix);
		}
		return true;
	}

	private static bool IdentityEndsWith(ItemClassificationInput item, string suffix)
	{
		if (!EndsWith(CleanName(item.PrefabName), suffix))
		{
			return EndsWith(CleanLocalizationToken(item.SharedName), suffix);
		}
		return true;
	}

	private static string CleanName(string value)
	{
		string text = (value ?? string.Empty).Trim();
		if (text.EndsWith("(Clone)", StringComparison.OrdinalIgnoreCase))
		{
			text = text.Substring(0, text.Length - "(Clone)".Length).TrimEnd();
		}
		return text;
	}

	private static string CleanLocalizationToken(string value)
	{
		string text = CleanName(value);
		if (text.StartsWith("$item_", StringComparison.OrdinalIgnoreCase))
		{
			text = text.Substring(6);
		}
		return text.Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty);
	}

	private static bool StartsWith(string value, string prefix)
	{
		return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
	}

	private static bool EndsWith(string value, string suffix)
	{
		return value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
	}

	private static bool Contains(string value, string fragment)
	{
		return (value ?? string.Empty).IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private static bool EqualsIgnoreCase(string left, string right)
	{
		return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
	}

	private static HashSet<string> Names(params string[] names)
	{
		return new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
	}
}
