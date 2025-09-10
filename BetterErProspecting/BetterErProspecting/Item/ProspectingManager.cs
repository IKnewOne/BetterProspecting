using System;
using System.Collections.Generic;
using System.Text;
using System.Timers;
using BetterErProspecting.Item.Data;
using HydrateOrDiedrate;
using HydrateOrDiedrate.Aquifer;
using HydrateOrDiedrate.Aquifer.ModData;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using Vintagestory.ServerMods;

namespace BetterErProspecting.Item;
public partial class ItemBetterErProspectingPick {

	// These are assholes
	public static Dictionary<string, string> specialOreCodeConversion = new Dictionary<string, string>() {
		// These have items different than the code used for the material. Funnily enough, both of them are child deposits
		{"nativegold", "gold" },
		{"nativesilver", "silver" }
	};
	// For now a few cases. The conversion is a public method, can extend from there.
	// I will assume basegame's logic of "_" meaning childnode
	public static string ConvertChildRocks(string code) {
		if (code == null)
			return null;

		var span = code.AsSpan();
		int idx = code.LastIndexOf('_');
		ReadOnlySpan<char> suffixSpan = idx >= 0 ? span.Slice(idx + 1) : span;

		string suffix = suffixSpan.ToString();

		if (specialOreCodeConversion.TryGetValue(suffix, out var value)) {
			return value;
		}

		return suffix;
	}

	public static bool generateReadigs(ICoreServerAPI sapi, IServerPlayer serverPlayer, ProPickWorkSpace ppws, BlockPos pos, Dictionary<string, int> codeToFoundOre, out PropickReading readings) {
		var world = sapi.World;
		LCGRandom Rnd = new LCGRandom(sapi.World.Seed);
		DepositVariant[] deposits = sapi.ModLoader.GetModSystem<GenDeposits>()?.Deposits;
		if (deposits == null) {
			readings = null;
			return false;

		}

		int chunkSize = GlobalConstants.ChunkSize;
		int mapHeight = world.BlockAccessor.GetTerrainMapheightAt(pos);
		int chunkBlocks = chunkSize * chunkSize * mapHeight;


		readings = new PropickReading();
		readings.Position = pos.ToVec3d();
		StringBuilder sb = new StringBuilder();
		bool didOreLevelUpscale = false;

		foreach (var foundOre in codeToFoundOre) {
			string oreCode = foundOre.Key;
			int empiricalAmount = foundOre.Value;


			var reading = new OreReading();
			reading.PartsPerThousand = (double)empiricalAmount / chunkBlocks * 1000;

			DepositVariant variant = ppws.depositsByCode[oreCode];
			var generator = variant.GeneratorInst;

			double? totalFactor = CalculatorManager.GetPercentile(generator, variant, empiricalAmount);

			if (totalFactor == null) {
				sb.Append($"[BetterEr Prospecting] Found no predefined calculator for {generator.GetType()})");

				sb.Append(", using default generator");
				IBlockAccessor blockAccess = world.BlockAccessor;
				int regsize = blockAccess.RegionSize;
				IMapRegion reg = world.BlockAccessor.GetMapRegion(pos.X / regsize, pos.Z / regsize);
				int lx = pos.X % regsize;
				int lz = pos.Z % regsize;

				IntDataMap2D map = reg.OreMaps[oreCode];
				int noiseSize = map.InnerSize;

				float posXInRegionOre = (float)lx / regsize * noiseSize;
				float posZInRegionOre = (float)lz / regsize * noiseSize;

				int oreDist = map.GetUnpaddedColorLerped(posXInRegionOre, posZInRegionOre);
				int[] blockColumn = ppws.GetRockColumn(pos.X, pos.Z);

				ppws.depositsByCode[oreCode].GeneratorInst.GetPropickReading(pos, oreDist, blockColumn, out double fakePpt, out double imaginationLandFactor);

				totalFactor = imaginationLandFactor;

				sb.AppendLine();
			}

			if (totalFactor <= PropickReading.MentionThreshold) {
				var debugString = $"[BetterEr Prospecting] Factor is below visibility: {totalFactor:0.####} for {oreCode}";

				if (sb.Length > 0) {
					sb.Append($", {totalFactor:0.####} for {oreCode}");
				} else {
					sb.Append(debugString);
				}

				if (config.AlwaysAddTraceOres) {
					if (config.AddToPoorOres) {
						totalFactor = 0.15; // It's actually ~1.3 but 5 engages monkey brain
					} else {
						totalFactor = PropickReading.MentionThreshold + 1e-6;
					}
				}
			}

			reading.TotalFactor = (double)totalFactor;
			readings.OreReadings[oreCode] = reading;
		}


		if (config.DebugMode && sb.Length > 0) {
			if (didOreLevelUpscale) {
				sb.AppendLine();
				sb.Append(config.AddToPoorOres ? "Modified to poor level" : "Modified to trace level");
			}
			serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, sb.ToString(), EnumChatType.Notification);
		}

		return true;
	}
	public static string getHandbookLinkOrName(IWorldAccessor world, IServerPlayer serverPlayer, string key) {
		var itemName = Lang.GetL(serverPlayer.LanguageCode, key);
		if (world.GetBlock(key) is Block block) {
			return $"<a href=\"handbook://{GuiHandbookItemStackPage.PageCodeForStack(new ItemStack(block))}\">{itemName}</a>";
		} else if (world.GetItem(key) is Vintagestory.API.Common.Item item) {
			return $"<a href=\"handbook://{GuiHandbookItemStackPage.PageCodeForStack(new ItemStack(item))}\">{itemName}</a>";
		} else {
			// Sometimes the block looks weird. Don't want to lose data
			return itemName;
		}
	}
	private static List<DelayedMessage> addMiscReadings(ICoreServerAPI sapi, IServerPlayer serverPlayer, PropickReading readings, BlockPos pos, List<DelayedMessage> delayedMessages = null) {
		delayedMessages ??= new List<DelayedMessage>();

		// Hydrate Or Diedrate
		if (sapi.ModLoader.IsModEnabled("hydrateordiedrate")) {
			hydrateOrDiedrate(sapi, readings, pos, delayedMessages);
		}

		return delayedMessages;
	}
	private static void hydrateOrDiedrate(ICoreServerAPI sapi, PropickReading readings, BlockPos pos, List<DelayedMessage> delayedMessages) {
		var world = sapi.World;

		HydrateOrDiedrateModSystem system = sapi.ModLoader.GetModSystem<HydrateOrDiedrateModSystem>();

		if (new Version(system.Mod.Info.Version) < new Version("2.2.12")) {
			delayedMessages.Add(new DelayedMessage("[BetterEr Prospecting] Please update HydrateOrDietrade for aquifer support"));
			return;
		}

		var chnData = AquiferManager.GetAquiferChunkData(world, pos)?.Data;
		if (chnData == null) {
			return;
		}

		var hydrateConfig = HydrateOrDiedrate.Config.ModConfig.Instance;

		if (hydrateConfig.GroundWater.ShowAquiferProspectingDataOnMap) {
			readings.OreReadings.Add(AquiferData.OreReadingKey, chnData);
		}

		delayedMessages.Add(new DelayedMessage(AquiferManager.GetAquiferDirectionHint(world, pos)));
	}

	#region Helpers
	public static bool IsOre(Block block, Dictionary<string, string> cache, out string key, out string typeKey) {
		key = null;
		typeKey = null;

		if (block.BlockMaterial != EnumBlockMaterial.Ore || block.Variant == null)
			return false;
		if (!block.Variant.TryGetValue("type", out string oreKey))
			return false;

		if (!cache.TryGetValue(oreKey, out typeKey)) {
			typeKey = ConvertChildRocks(oreKey);
			cache[oreKey] = typeKey;
		}

		key = "ore-" + typeKey;
		return true;
	}

	public static bool IsOre(Block block, Dictionary<string, string> cache, out string key) {
		return IsOre(block, cache, out key, out _);
	}

	public static bool IsRock(Block block, Dictionary<string, string> cache, out string key, out string rockKey) {
		key = null;
		rockKey = null;
		if (block.Variant == null || !block.Variant.TryGetValue("rock", out rockKey))
			return false;

		if (!cache.TryGetValue(rockKey, out var cached)) {
			cached = "rock-" + rockKey;
			cache[rockKey] = cached;
		}

		key = cache[rockKey];
		return true;
	}

	public static bool IsRock(Block block, Dictionary<string, string> cache, out string key) {
		return IsRock(block, cache, out key, out _);
	}




	public static bool isPropickable(Block block) { return block?.Attributes?["propickable"].AsBool(false) == true; }
	private Timer _reloadDebounce;
	private void DebounceReload(Action action) {
		if (_reloadDebounce == null) {
			_reloadDebounce = new Timer(1000);
			_reloadDebounce.AutoReset = false;
			_reloadDebounce.Elapsed += (s, e) => action();
		}

		_reloadDebounce.Stop();
		_reloadDebounce.Start();
	}
	#endregion
}
