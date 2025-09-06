using System;
using System.Collections.Generic;
using System.Linq;
using BetterErProspecting.Helper;
using BetterErProspecting.Interface;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using Vintagestory.ServerMods;

namespace BetterErProspecting;
public class ItemBetterErProspectingPick : ItemProspectingPick {
	SkillItem[]? toolModes;
	public static ILogger Logger => BetterErProspectingModSystem.Logger;

	private enum Mode {
		density,
		node,
		proximity,
		stone,
		borehole
	}

	private Dictionary<Mode, AssetLocation> modeToAsset = new Dictionary<Mode, AssetLocation>() {
		{ Mode.density, new AssetLocation("textures/icons/heatmap.svg") },
		{ Mode.node, new AssetLocation("textures/icons/rocks.svg") },
		{ Mode.proximity, new AssetLocation("textures/icons/worldmap/spiral.svg") },
		{ Mode.stone, new AssetLocation("bettererprospecting", "textures/icons/probe_stone.svg") },
		{ Mode.borehole, new AssetLocation("bettererprospecting", "textures/icons/probe_borehole.svg") }
	};

	public static ModConfig config => ModConfig.Instance;
	public override void OnLoaded(ICoreAPI api) {

		toolModes = ObjectCacheUtil.GetOrCreate<SkillItem[]>(api, "proPickToolModes", () => {
			List<SkillItem> modes = new List<SkillItem>();

			if (config.NewDensityMode) {
				modes.Add(new SkillItem {
					Code = new AssetLocation(Mode.density.ToString()),
					Name = Lang.Get("Density Search Mode (Long range, percentage based search)")
				});
			} else {
				modes.Add(new SkillItem {
					Code = new AssetLocation(Mode.density.ToString()),
					Name = Lang.Get("Density Search Mode (Long range, chance based search)")
				});
			}

			if (int.Parse(api.World.Config.GetString("propickNodeSearchRadius")) > 0) {
				modes.Add(new SkillItem {
					Code = new AssetLocation(Mode.node.ToString()),
					Name = Lang.Get("Node Search Mode (Medim range, exact search)")
				});
			}

			if (config.AddProximityMode) {
				modes.Add(new SkillItem {
					Code = new AssetLocation(Mode.proximity.ToString()),
					Name = Lang.Get("Proximity (Short range, exact search)")
				});
			}

			if (config.AddBoreHoleMode) {
				modes.Add(new SkillItem {
					Code = new AssetLocation(Mode.borehole.ToString()),
					Name = Lang.Get("Borehole Mode (Vertical line based search)")
				});
			}

			if (config.AddStoneMode) {
				modes.Add(new SkillItem {
					Code = new AssetLocation(Mode.stone.ToString()),
					Name = Lang.Get("Stone Mode (Long range, distance search for stone)")
				});
			}

			return modes.ToArray();
		});

		if (api is ICoreClientAPI capi) {
			for (int i = 0; i < toolModes.Length; i++) {
				var m = toolModes[i];
				Mode mode = (Mode)Enum.Parse(typeof(Mode), m.Code.Path, true);
				m.WithIcon(capi, capi.Gui.LoadSvgWithPadding(modeToAsset[mode], 48, 48, 5, ColorUtil.WhiteArgb));
				m.TexturePremultipliedAlpha = false;
			}
		}

		base.OnLoaded(api);
	}

	public override SkillItem[] GetToolModes(ItemSlot slot, IClientPlayer forPlayer, BlockSelection blockSel) {
		return toolModes;
	}

	public override int GetToolMode(ItemSlot slot, IPlayer byPlayer, BlockSelection blockSel) {
		return Math.Min(toolModes.Length - 1, slot.Itemstack.Attributes.GetInt("toolMode"));
	}

	public override float OnBlockBreaking(IPlayer player, BlockSelection blockSel, ItemSlot itemslot, float remainingResistance, float dt, int counter) {
		float remain = base.OnBlockBreaking(player, blockSel, itemslot, remainingResistance, dt, counter);
		int toolMode = GetToolMode(itemslot, player, blockSel);

		remain = (remain + remainingResistance) / 2.2f;
		return remain;
	}

	public override bool OnBlockBrokenWith(IWorldAccessor world, Entity byEntity, ItemSlot itemslot, BlockSelection blockSel, float dropQuantityMultiplier = 1) {
		int tm = GetToolMode(itemslot, (byEntity as EntityPlayer).Player, blockSel);
		SkillItem skillItem = toolModes[tm];
		Mode toolMode = (Mode)Enum.Parse(typeof(Mode), skillItem.Code.Path, true);

		int damage = 1;

		switch (toolMode) {
			case Mode.density:
				ModProbeDensityMode(world, byEntity, itemslot, blockSel, out damage);
				break;
			case Mode.node:
				base.ProbeBlockNodeMode(world, byEntity, itemslot, blockSel, int.Parse(api.World.Config.GetString("propickNodeSearchRadius")));
				break;
			case Mode.proximity:
				ProbeProximity(world, byEntity, itemslot, blockSel, out damage);
				break;

			case Mode.stone:
				ProbeStone(world, byEntity, itemslot, blockSel, out damage);
				break;

			case Mode.borehole:
				ProbeBorehole(world, byEntity, itemslot, blockSel, out damage);
				break;
		}

		if (DamagedBy != null && DamagedBy.Contains(EnumItemDamageSource.BlockBreaking)) {
			DamageItem(world, byEntity, itemslot, damage);
		}

		return true;
	}

	// Modded Density amount-based search. Square with chunkSize radius around current block. Whole mapheight
	protected virtual void ModProbeDensityMode(IWorldAccessor world, Entity byEntity, ItemSlot itemslot, BlockSelection blockSel, out int damage) {
		damage = 1;

		if (!config.NewDensityMode) {
			base.ProbeBlockDensityMode(world, byEntity, itemslot, blockSel);
			return;
		}

		IPlayer? byPlayer = null;
		if (byEntity is EntityPlayer)
			byPlayer = world.PlayerByUid(((EntityPlayer)byEntity).PlayerUID);

		Block block = world.BlockAccessor.GetBlock(blockSel.Position);
		block.OnBlockBroken(world, blockSel.Position, byPlayer, 0);

		if (!isPropickable(block)) {
			damage = 1;
			return;
		}

		IServerPlayer? serverPlayer = byPlayer as IServerPlayer;
		if (serverPlayer == null)
			return;

		damage = 3;
		int chunkSize = GlobalConstants.ChunkSize;
		int mapHeight = world.BlockAccessor.GetTerrainMapheightAt(blockSel.Position);
		int chunkBlocks = chunkSize * chunkSize * mapHeight;
		String[] blacklistedBlocks = ["flint", "silver", "quartz"];


		ProPickWorkSpace ppws = ObjectCacheUtil.TryGet<ProPickWorkSpace>(api, "propickworkspace");
		Dictionary<string, (int Count, string OriginalKey)> codeToFoundOre = new();

		BlockPos blockPos = blockSel.Position.Copy();
		api.World.BlockAccessor.WalkBlocks(new BlockPos(blockPos.X - chunkSize, mapHeight, blockPos.Z - chunkSize), new BlockPos(blockPos.X + chunkSize, 0, blockPos.Z + chunkSize), delegate (Block nblock, int x, int y, int z) {

			// Special case because of course halite is a rock
			if (nblock.Code.Path.Contains("halite")) {
				(int Count, string OriginalKey) entry = codeToFoundOre.GetValueOrDefault("halite", (0, "halite"));
				codeToFoundOre["halite"] = (entry.Count + 1, nblock.Code.Path);
			}

			if (nblock.BlockMaterial == EnumBlockMaterial.Ore && nblock.Variant.ContainsKey("type")) {
				string originalKey = nblock.Variant["type"];
				string key = ConvertChildRocks(originalKey);

				if (!blacklistedBlocks.Contains(key)) {
					(int Count, string OriginalKey) entry = codeToFoundOre.GetValueOrDefault(key, (0, originalKey));
					codeToFoundOre[key] = (entry.Item1 + 1, originalKey);
				}
			}
		});

		var existingPages = ppws.depositsByCode.Keys.ToList();
		List<string> missingPageCode = codeToFoundOre.Keys.Where(k => !existingPages.Contains(k)).ToList();
		missingPageCode.ForEach(k => codeToFoundOre.Remove(k));

		if (!generateReadigs(world, ppws, blockPos, codeToFoundOre, out PropickReading readings))
			return;

		if (config.DebugMode) {
			// We want original key because it might have gotten transformed by ConvertChildRocks
			if (missingPageCode.Count > 0) {
				var pairs = missingPageCode.Select(k => $"{k}:{codeToFoundOre[k].OriginalKey}");
				serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, Lang.GetL(serverPlayer.LanguageCode, $"[BetterErProspecting] Missing page codes (missing in prospectable list): {string.Join(", ", pairs)}"), EnumChatType.Notification);
				serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, Lang.GetL(serverPlayer.LanguageCode, $"[BetterErProspecting] Consider reporting to mod developer if you think there's been an error"), EnumChatType.Notification);
			}
		}


		var textResults = readings.ToHumanReadable(serverPlayer.LanguageCode, ppws.pageCodes);
		serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, textResults, EnumChatType.Notification);
		world.Api.ModLoader.GetModSystem<ModSystemOreMap>()?.DidProbe(readings, serverPlayer);

	}


	// Radius-based search
	protected virtual void ProbeProximity(IWorldAccessor world, Entity byEntity, ItemSlot itemslot, BlockSelection blockSel, out int damage) {
		damage = config.ProximityDmg;

		int radius = config.ProximitySearchRadius;
		IPlayer? byPlayer = null;

		if (byEntity is EntityPlayer) {
			byPlayer = world.PlayerByUid(((EntityPlayer)byEntity).PlayerUID);
		}

		Block block = world.BlockAccessor.GetBlock(blockSel.Position);
		block.OnBlockBroken(world, blockSel.Position, byPlayer, 0);

		if (!isPropickable(block)) {
			damage = 1;
			return;
		}

		IServerPlayer? serverPlayer = byPlayer as IServerPlayer;
		if (serverPlayer == null)
			return;

		BlockPos pos = blockSel.Position.Copy();
		int closestOre = -1;

		api.World.BlockAccessor.WalkBlocks(pos.AddCopy(radius, radius, radius), pos.AddCopy(-radius, -radius, -radius), (nblock, x, y, z) => {
			if (nblock.BlockMaterial == EnumBlockMaterial.Ore && nblock.Variant.ContainsKey("type")) {
				var distanceTo = (int)Math.Round(pos.DistanceTo(x, y, z));

				if (closestOre == -1 || closestOre > distanceTo) {
					closestOre = distanceTo;
				}
			}
		});

		if (closestOre != -1) {
			serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, Lang.GetL(serverPlayer.LanguageCode, "Closest ore is {0} blocks away!", closestOre), EnumChatType.Notification);
		} else {
			serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, Lang.GetL(serverPlayer.LanguageCode, "No ore found in {0} block radius.", radius), EnumChatType.Notification);
		}
	}

	// Radius-based search
	protected virtual void ProbeStone(IWorldAccessor world, Entity byEntity, ItemSlot itemslot, BlockSelection blockSel, out int damage) {
		damage = config.StoneDmg;
		int walkRadius = config.StoneSearchRadius;

		IPlayer? byPlayer = null;
		if (byEntity is EntityPlayer)
			byPlayer = world.PlayerByUid(((EntityPlayer)byEntity).PlayerUID);

		Block block = world.BlockAccessor.GetBlock(blockSel.Position);
		block.OnBlockBroken(world, blockSel.Position, byPlayer, 0);

		if (!isPropickable(block)) {
			damage = 1;
			return;
		}

		IServerPlayer? serverPlayer = byPlayer as IServerPlayer;
		if (serverPlayer == null)
			return;

		serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, Lang.GetL(serverPlayer.LanguageCode, $"Area sample taken for a radius of {walkRadius}:"), EnumChatType.Notification);

		Dictionary<string, int> firstBlockDistance = new Dictionary<string, int>();

		BlockPos blockPos = blockSel.Position.Copy();
		api.World.BlockAccessor.WalkBlocks(blockPos.AddCopy(walkRadius, walkRadius, walkRadius), blockPos.AddCopy(-walkRadius, -walkRadius, -walkRadius), delegate (Block nblock, int x, int y, int z) {
			if (nblock.Variant.ContainsKey("rock")) {
				string key = "rock-" + nblock.Variant["rock"];
				int distance = (int)blockSel.Position.DistanceTo(new BlockPos(x, y, z));
				if (!firstBlockDistance.ContainsKey(key) || distance < firstBlockDistance[key]) {
					firstBlockDistance[key] = distance;
				}
			}

		});

		List<KeyValuePair<string, int>> list = firstBlockDistance.ToList();
		if (list.Count == 0) {
			serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, Lang.GetL(serverPlayer.LanguageCode, "No rocks neaby"), EnumChatType.Notification);
			return;
		}

		serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, Lang.GetL(serverPlayer.LanguageCode, "Found the following rock types"), EnumChatType.Notification);
		foreach (KeyValuePair<string, int> item in list) {
			string l = Lang.GetL(serverPlayer.LanguageCode, item.Key);
			string capitalized = char.ToUpper(l[0]) + l.Substring(1);
			serverPlayer.SendMessage(
				GlobalConstants.InfoLogChatGroup,
				Lang.GetL(serverPlayer.LanguageCode, $"{capitalized}: {item.Value} block(s) away"),
				EnumChatType.Notification
			);
		}


	}

	// Line-based search
	// Borehole deez nuts lmao gottem
	protected virtual void ProbeBorehole(IWorldAccessor world, Entity byEntity, ItemSlot itemslot, BlockSelection blockSel, out int damage) {
		damage = config.BoreholeDmg;

		IPlayer? byPlayer = null;
		if (byEntity is EntityPlayer)
			byPlayer = world.PlayerByUid(((EntityPlayer)byEntity).PlayerUID);

		Block block = world.BlockAccessor.GetBlock(blockSel.Position);
		block.OnBlockBroken(world, blockSel.Position, byPlayer, 0);

		if (!isPropickable(block)) {
			damage = 1;
			return;
		}

		IServerPlayer? serverPlayer = byPlayer as IServerPlayer;
		if (serverPlayer == null)
			return;

		BlockFacing face = blockSel.Face;

		if (!config.BoreholeScansOre && !config.BoreholeScansStone) {
			serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, Lang.GetL(serverPlayer.LanguageCode, "Borehole has not been configured to search for either type"), EnumChatType.Notification);
			return;
		}

		// It's MY mod. And i get to decide what's important for immersion:tm:
		if (face != BlockFacing.UP) {
			serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, Lang.GetL(serverPlayer.LanguageCode, "Bore samples can only be taken from upper side of the block"), EnumChatType.Notification);
			return;
		}

		serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, Lang.GetL(serverPlayer.LanguageCode, "Bore sample taken:"), EnumChatType.Notification);

		var descendingOrderBlocks = new SortedSet<string>();

		BlockPos searchPos = blockSel.Position.Copy();
		while (searchPos.Y > 0) {

			Block nblock = api.World.BlockAccessor.GetBlock(searchPos);
			int distance = (int)blockSel.Position.DistanceTo(searchPos);

			if (config.BoreholeScansOre && nblock.BlockMaterial == EnumBlockMaterial.Ore && nblock.Variant.ContainsKey("type")) {
				string key = "ore-" + nblock.Variant["type"];
				descendingOrderBlocks.Add(key);
			}

			if (config.BoreholeScansStone && nblock.Variant.ContainsKey("rock")) {
				string key = "rock-" + nblock.Variant["rock"];
				descendingOrderBlocks.Add(key);
			}

			searchPos.Y -= 1;
		}

		if (descendingOrderBlocks.Count == 0) {
			serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, Lang.GetL(serverPlayer.LanguageCode, "No results found"), EnumChatType.Notification);
		} else {
			var oreNames = descendingOrderBlocks.Select(val => Lang.GetL(serverPlayer.LanguageCode, val));
			serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, Lang.GetL(serverPlayer.LanguageCode, "Found the following blocks: ") + string.Join(", ", oreNames), EnumChatType.Notification);
		}

		return;
	}

	// Hey, you. Yes, you. Add your patch for the density here. I even kept it public
	// Check ProPickWorkSpace.pageCodes and respective child ore codes
	public Dictionary<string, string> codeToPageConversion = new Dictionary<string, string>() {
		{"nativegold", "gold" },
		{"nativesilver", "silver" },
		{"peridot", "peridot" }, //spammy

	};
	// For now a few cases. The conversion is a public method, can extend from there.
	// I will assume basegame's logic of "_" meaning childnode
	public string ConvertChildRocks(string code) {
		int idx = code.LastIndexOf("_");
		if (idx >= 0) {
			string suffix = code.Substring(idx + 1);

			if (codeToPageConversion.TryGetValue(suffix, out string value)) {
				return value;
			} else {
				Logger.Warning("[BetterEr Prospecting] tried to convert child ore {0}, found no value. Intentional or missed combo ?", suffix);
				return suffix;
			}
		}

		return code;
	}



	private bool generateReadigs(IWorldAccessor world, ProPickWorkSpace ppws, BlockPos pos, Dictionary<string, (int Count, string OriginalKey)> codeToFoundOre, out PropickReading readings) {


		LCGRandom Rnd = new LCGRandom(api.World.Seed);
		DepositVariant[] deposits = api.ModLoader.GetModSystem<GenDeposits>()?.Deposits;
		if (deposits == null) {
			readings = null;
			return false;

		}

		int chunkSize = GlobalConstants.ChunkSize;
		int mapHeight = world.BlockAccessor.GetTerrainMapheightAt(pos);
		int chunkBlocks = chunkSize * chunkSize * mapHeight;


		readings = new PropickReading();
		readings.Position = pos.ToVec3d();

		foreach (var foundOre in codeToFoundOre) {
			string oreCode = foundOre.Key;
			int empiricalAmount = foundOre.Value.Count;


			var reading = new OreReading();
			reading.PartsPerThousand = (double)empiricalAmount / chunkBlocks * 1000;


			var variant = ppws.depositsByCode[oreCode];
			var generator = variant.GeneratorInst;

			double totalFactor;

			if (generator is DiscDepositGenerator dGen) {
				totalFactor = DiscDistributionCalculator.getPercentileOfEmpiricalValue(empiricalAmount, dGen, variant);
			} else if (generator is IGeneratorPercentileProvider iGen) {
				totalFactor = ((IGeneratorPercentileProvider)iGen).getPercentileOfEmpiricalValue(empiricalAmount, variant);
			} else {
				// Fallback to generic factor. Not entirely accurate but we can't leave it empty either

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
			}

			reading.TotalFactor = totalFactor;
			readings.OreReadings[oreCode] = reading;
		}

		return true;
	}

	private bool isPropickable(Block block) {
		return block?.Attributes?["propickable"].AsBool(false) == true;
	}
}
