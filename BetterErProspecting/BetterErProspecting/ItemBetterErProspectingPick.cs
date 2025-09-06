using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
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

	protected virtual void ModProbeDensityMode(IWorldAccessor world, Entity byEntity, ItemSlot itemslot, BlockSelection blockSel, out int damage) {
		damage = 1;

		if (!config.NewDensityMode) {
			base.ProbeBlockDensityMode(world, byEntity, itemslot, blockSel);
			return;
		}

		// Modded Density amount-based search. Radius based but whole Y chunk range
		// Make configurable ? Value based on the wiki strategy suggestion of 40 blocks, thus the radius is 20 because 41 = 20 * 2 obviously
		damage = 3;
		int radius = 20;
		String[] blacklistedBlocks = ["flint", "silver", "quartz"];

		// Should be good enough
		int searchStart = Math.Max(TerraGenConfig.seaLevel + 20, blockSel.Position.Y + 20);


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


		// TODO: Try to calculate the values and insert into the prospecting map manually. Kinda harder, will try another approach but leaving this here
		List<EnumBlockMaterial> countableMaterials = new List<EnumBlockMaterial>() { EnumBlockMaterial.Ore, EnumBlockMaterial.Stone, EnumBlockMaterial.Sand, EnumBlockMaterial.Gravel };
		Dictionary<string, int> quantityFound = new Dictionary<string, int>();
		int countedBlocks = 0; // Specifically geological ones. Maybe add another material ?

		BlockPos blockPos = blockSel.Position.Copy();

		api.World.BlockAccessor.WalkBlocks(
			new BlockPos(blockPos.X - radius, searchStart, blockPos.Z - radius),
			new BlockPos(blockPos.X + radius, 0, blockPos.Z + radius),
		delegate (Block nblock, int x, int y, int z) {

			if (countableMaterials.Contains(nblock.BlockMaterial)) {
				countedBlocks += 1;
			}

			// Special case because of course halite is a rock
			if (nblock.Code.Path.Contains("halite")) {
				int value = 0;
				quantityFound.TryGetValue("halite", out value);
				quantityFound["halite"] = value + 1;
			}


			if (nblock.BlockMaterial == EnumBlockMaterial.Ore && nblock.Variant.ContainsKey("type")) {
				string key = nblock.Variant["type"];

				key = ConvertChildRocks(key);

				if (!blacklistedBlocks.Contains(key)) {
					int value = 0;
					quantityFound.TryGetValue(key, out value);
					quantityFound[key] = value + 1;
				}

			}
		});

		var propickResults = base.GenProbeResults(world, blockSel.Position);
		var ppws = ObjectCacheUtil.TryGet<ProPickWorkSpace>(api, "propickworkspace");

		// Remove non-existing ores
		var keysToRemove = propickResults.OreReadings.Keys.Where(key => !quantityFound.ContainsKey(key)).ToList();
		if (keysToRemove.Count > 0) {
			Logger.Debug("[BetterErProspecting] Removing non-existing ores from results: {0}", string.Join(", ", keysToRemove));
		}

		foreach (var key in keysToRemove) { propickResults.OreReadings.Remove(key); }

		// Recalculate ppt based on real numbers ( probably need to expand the list of countable materials )
		foreach (var kvp in quantityFound) {
			string oreType = kvp.Key;
			int foundCount = kvp.Value;
			double realAndBasedPpt = (double)foundCount / countedBlocks * 1000;

			if (propickResults.OreReadings.TryGetValue(oreType, out var oreReading)) {
				oreReading.PartsPerThousand = realAndBasedPpt;
			} else {

				if (ppws.pageCodes.ContainsKey(oreType)) {
					Logger.Debug("[BetterErProspecting] Found ore in reality that didn't exist in ore readings: {0}, found {1}. Adding with base factor", oreType, foundCount);

					propickResults.OreReadings[oreType] = new OreReading {
						DepositCode = null, // Not seeing its usage outside of "unknown"
						TotalFactor = 0.026, // Need to be over 0.025 to be visible. There might be a more clever solution,
											 // but since low factor usually means low amounts and we found a place where
											 // the map and the rock factor is garbage, this works out well in the end
						PartsPerThousand = realAndBasedPpt
					};

				} else {
					// Rip easy olivine
					Logger.Warning("[BetterErProspecting] Counted ore that isn't part of pageCodes. Ignored {0}", oreType);
				}
			}
		}

		var textResults = propickResults.ToHumanReadable(serverPlayer.LanguageCode, ppws.pageCodes);
		serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, textResults, EnumChatType.Notification);
		world.Api.ModLoader.GetModSystem<ModSystemOreMap>()?.DidProbe(propickResults, serverPlayer);

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
		{"peridot-rough", "peridot" }, //uncertain

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

	private bool isPropickable(Block block) {
		return block?.Attributes?["propickable"].AsBool(false) == true;
	}
}
