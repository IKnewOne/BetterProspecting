using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Timers;
using BetterErProspecting.Config;
using HarmonyLib;
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

namespace BetterErProspecting.Item;
public class ItemBetterErProspectingPick : ItemProspectingPick {
	SkillItem[] toolModes;
	public static ILogger Logger => CoreModSystem.Logger;
	private enum Mode {
		density,
		node,
		proximity,
		stone,
		borehole
	}

	class ModeData {
		public AssetLocation Asset;
		public LoadedTexture Texture;
		public SkillItem Skill;

		public ModeData(Mode mode, string assetPath, string domain = null) {
			Asset = domain == null ? new AssetLocation(assetPath) : new AssetLocation(domain, assetPath);
			Skill = new SkillItem { Code = new AssetLocation(mode.ToString()) };
		}
	}

	private Dictionary<Mode, ModeData> modeData = new Dictionary<Mode, ModeData>() {
	{ Mode.density, new ModeData(Mode.density, "textures/icons/heatmap.svg") },
	{ Mode.node, new ModeData(Mode.node, "textures/icons/rocks.svg") },
	{ Mode.proximity, new ModeData(Mode.proximity, "textures/icons/worldmap/spiral.svg") },
	{ Mode.stone, new ModeData(Mode.stone, "textures/icons/probe_stone.svg", "bettererprospecting") },
	{ Mode.borehole, new ModeData(Mode.borehole, "textures/icons/probe_borehole.svg", "bettererprospecting") }
};

	public static ModConfig config => ModConfig.Instance;
	private Timer _reloadDebounce;
	public override void OnLoaded(ICoreAPI api) {

		GenerateToolModes(api);

		CoreModSystem.SettingChanged += setting => {
			string[] settingToReloadFor = [nameof(ModConfig.NewDensityMode), nameof(ModConfig.AddBoreHoleMode), nameof(ModConfig.AddStoneMode), nameof(ModConfig.AddProximityMode)];

			if (settingToReloadFor.Contains(setting.YamlCode)) {
				DebounceReload(() => { GenerateToolModes(api); });
			}
		};

		base.OnLoaded(api);
	}

	private void GenerateToolModes(ICoreAPI api) {
		ObjectCacheUtil.Delete(api, "proPickToolModes");
		toolModes = ObjectCacheUtil.GetOrCreate(api, "proPickToolModes", () => {
			List<SkillItem> modes = new List<SkillItem>();

			// Density mode (two possible names, same SkillItem)
			if (config.NewDensityMode) {
				modeData[Mode.density].Skill.Name = Lang.Get("Density Search Mode (Long range, real blocks based search)");
			} else {
				modeData[Mode.density].Skill.Name = Lang.Get("Density Search Mode (Long range, statistic based search)");
			}
			modes.Add(modeData[Mode.density].Skill);

			// Node mode
			if (int.Parse(api.World.Config.GetString("propickNodeSearchRadius")) > 0) {
				modeData[Mode.node].Skill.Name = Lang.Get("Node Search Mode (Medium range, exact search)");
				modes.Add(modeData[Mode.node].Skill);
			}

			// Proximity mode
			if (config.AddProximityMode) {
				modeData[Mode.proximity].Skill.Name = Lang.Get("Proximity (Short range, exact search)");
				modes.Add(modeData[Mode.proximity].Skill);
			}

			// Borehole mode
			if (config.AddBoreHoleMode) {
				modeData[Mode.borehole].Skill.Name = Lang.Get("Borehole Mode (Vertical line based search)");
				modes.Add(modeData[Mode.borehole].Skill);
			}

			// Stone mode
			if (config.AddStoneMode) {
				modeData[Mode.stone].Skill.Name = Lang.Get("Stone Mode (Long range, distance search for stone)");
				modes.Add(modeData[Mode.stone].Skill);
			}

			return modes.ToArray();
		});
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

		IPlayer byPlayer = null;
		if (byEntity is EntityPlayer)
			byPlayer = world.PlayerByUid(((EntityPlayer)byEntity).PlayerUID);

		Block block = world.BlockAccessor.GetBlock(blockSel.Position);
		block.OnBlockBroken(world, blockSel.Position, byPlayer, 0);

		if (!isPropickable(block)) {
			damage = 1;
			return;
		}

		IServerPlayer serverPlayer = byPlayer as IServerPlayer;
		if (serverPlayer == null)
			return;

		damage = 3;
		int chunkSize = GlobalConstants.ChunkSize;
		int mapHeight = world.BlockAccessor.GetTerrainMapheightAt(blockSel.Position);
		int chunkBlocks = chunkSize * chunkSize * mapHeight;
		string[] blacklistedBlocks = ["flint", "quartz"];


		ProPickWorkSpace ppws = ObjectCacheUtil.TryGet<ProPickWorkSpace>(api, "propickworkspace");
		Dictionary<string, (int Count, string OriginalKey)> codeToFoundOre = new();

		BlockPos searchedBlock = blockSel.Position.Copy();
		api.World.BlockAccessor.WalkBlocks(new BlockPos(searchedBlock.X - chunkSize, mapHeight, searchedBlock.Z - chunkSize), new BlockPos(searchedBlock.X + chunkSize, 0, searchedBlock.Z + chunkSize), delegate (Block nblock, int x, int y, int z) {

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
		var missingPairs = missingPageCode.ToDictionary(k => k, k => codeToFoundOre[k].OriginalKey);
		missingPageCode.ForEach(k => codeToFoundOre.Remove(k));

		if (!generateReadigs(world, serverPlayer, ppws, blockSel.Position, codeToFoundOre, out PropickReading readings))
			return;

		if (config.DebugMode && missingPairs.Count > 0) {
			// We want original key because it might have gotten transformed by ConvertChildRocks
			var pairs = missingPairs.Select(kv => $"{kv.Key}:{kv.Value}");
			serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, Lang.GetL(serverPlayer.LanguageCode, $"[BetterEr Prospecting] Missing page codes (missing in prospectable list): {string.Join(", ", pairs)}"), EnumChatType.Notification);
			serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, Lang.GetL(serverPlayer.LanguageCode, $"[BetterEr Prospecting] Consider reporting to mod developer if you think there's been an error"), EnumChatType.Notification);

		}

		// There are some messages that we process here, but should be sent after
		List<DelayedMessage> delayedMessages = addMiscReadings(world, serverPlayer, readings, blockSel.Position);

		var textResults = readings.ToHumanReadable(serverPlayer.LanguageCode, ppws.pageCodes);
		serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, textResults, EnumChatType.Notification);

		delayedMessages.ForEach(msg => msg.Send(serverPlayer));

		world.Api.ModLoader.GetModSystem<ModSystemOreMap>()?.DidProbe(readings, serverPlayer);

	}

	// Radius-based search
	protected virtual void ProbeProximity(IWorldAccessor world, Entity byEntity, ItemSlot itemslot, BlockSelection blockSel, out int damage) {
		damage = config.ProximityDmg;

		int radius = config.ProximitySearchRadius;
		IPlayer byPlayer = null;

		if (byEntity is EntityPlayer) {
			byPlayer = world.PlayerByUid(((EntityPlayer)byEntity).PlayerUID);
		}

		Block block = world.BlockAccessor.GetBlock(blockSel.Position);
		block.OnBlockBroken(world, blockSel.Position, byPlayer, 0);

		if (!isPropickable(block)) {
			damage = 1;
			return;
		}

		IServerPlayer serverPlayer = byPlayer as IServerPlayer;
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

		IPlayer byPlayer = null;
		if (byEntity is EntityPlayer)
			byPlayer = world.PlayerByUid(((EntityPlayer)byEntity).PlayerUID);

		Block block = world.BlockAccessor.GetBlock(blockSel.Position);
		block.OnBlockBroken(world, blockSel.Position, byPlayer, 0);

		if (!isPropickable(block)) {
			damage = 1;
			return;
		}

		IServerPlayer serverPlayer = byPlayer as IServerPlayer;
		if (serverPlayer == null)
			return;

		StringBuilder sb = new StringBuilder();

		sb.AppendLine(Lang.GetL(serverPlayer.LanguageCode, $"Area sample taken for a radius of {walkRadius}:"));

		// Int is either distance or count
		Dictionary<string, int> rockInfo = new Dictionary<string, int>();

		BlockPos blockPos = blockSel.Position.Copy();
		api.World.BlockAccessor.WalkBlocks(blockPos.AddCopy(walkRadius, walkRadius, walkRadius), blockPos.AddCopy(-walkRadius, -walkRadius, -walkRadius), delegate (Block block, int x, int y, int z) {
			if (block.Variant.ContainsKey("rock")) {
				var key = "rock-" + block.Variant["rock"];

				if (config.StonePercentSearch) {
					int count = rockInfo.GetValueOrDefault(key, 0);
					rockInfo[key] = ++count;
				} else {
					int distance = (int)blockSel.Position.DistanceTo(new BlockPos(x, y, z));
					if (!rockInfo.ContainsKey(key) || distance < rockInfo[key]) {
						rockInfo[key] = distance;
					}
				}

			}

		});

		if (rockInfo.Count == 0) {
			serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, Lang.GetL(serverPlayer.LanguageCode, "No rocks neaby"), EnumChatType.Notification);
			return;
		}


		sb.AppendLine(Lang.GetL(serverPlayer.LanguageCode, "Found the following rock types"));


		int totalRocks = rockInfo.Values.Sum();
		List<KeyValuePair<string, int>> output;

		if (config.StonePercentSearch) {
			output = rockInfo.OrderByDescending(kvp => kvp.Value).ToList();
		} else {
			output = rockInfo.OrderBy(kvp => kvp.Value).ToList();
		}


		foreach ((string key, int amount) in output) {
			string itemLink = getHandbookLinkOrName(world, serverPlayer, key);

			if (config.StonePercentSearch) {
				double percent = amount * 100.0 / totalRocks;
				percent = percent > 0.01 ? percent : 0.01;
				sb.AppendLine(Lang.GetL(serverPlayer.LanguageCode, $"{itemLink}: {percent:0.##} %"));
			} else {
				sb.AppendLine(Lang.GetL(serverPlayer.LanguageCode, $"{itemLink}: {amount} block(s) away"));
			}

		}
		serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, sb.ToString(), EnumChatType.Notification);
	}

	// Line-based search
	// Borehole deez nuts lmao gottem
	protected virtual void ProbeBorehole(IWorldAccessor world, Entity byEntity, ItemSlot itemslot, BlockSelection blockSel, out int damage) {
		damage = config.BoreholeDmg;

		IPlayer byPlayer = null;
		if (byEntity is EntityPlayer)
			byPlayer = world.PlayerByUid(((EntityPlayer)byEntity).PlayerUID);

		Block block = world.BlockAccessor.GetBlock(blockSel.Position);
		block.OnBlockBroken(world, blockSel.Position, byPlayer, 0);

		if (!isPropickable(block)) {
			damage = 1;
			return;
		}

		IServerPlayer serverPlayer = byPlayer as IServerPlayer;
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

		StringBuilder sb = new StringBuilder();
		sb.AppendLine(Lang.GetL(serverPlayer.LanguageCode, "Bore sample taken:"));

		var blockKeys = new SortedSet<string>();

		BlockPos searchPos = blockSel.Position.Copy();
		while (searchPos.Y > 0) {

			Block nblock = api.World.BlockAccessor.GetBlock(searchPos);
			int distance = (int)blockSel.Position.DistanceTo(searchPos);

			if (config.BoreholeScansOre && nblock.BlockMaterial == EnumBlockMaterial.Ore && nblock.Variant.ContainsKey("type")) {
				string key = "ore-" + nblock.Variant["type"];
				blockKeys.Add(key);
			}

			if (config.BoreholeScansStone && nblock.Variant.ContainsKey("rock")) {
				string key = "rock-" + nblock.Variant["rock"];
				blockKeys.Add(key);
			}

			searchPos.Y -= 1;
		}

		if (blockKeys.Count == 0) {
			sb.AppendLine(Lang.GetL(serverPlayer.LanguageCode, "No results found"));
		} else {
			var linkedNames = blockKeys.Select(key => getHandbookLinkOrName(world, serverPlayer, key)).ToList();
			sb.AppendLine(Lang.GetL(serverPlayer.LanguageCode, "Found the following blocks: ") + string.Join(", ", linkedNames));
		}

		serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, sb.ToString(), EnumChatType.Notification);

		return;
	}

	// Check ProPickWorkSpace.pageCodes and respective child ore codes
	public Dictionary<string, string> codeToPageConversion = new Dictionary<string, string>() {
		{"nativegold", "gold" },
		{"nativesilver", "silver" },
		{"peridot", "peridot" }, //spammy
		{"olivine", "olivine" }, //spammy

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

	private bool generateReadigs(IWorldAccessor world, IServerPlayer serverPlayer, ProPickWorkSpace ppws, BlockPos pos, Dictionary<string, (int Count, string OriginalKey)> codeToFoundOre, out PropickReading readings) {


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
		StringBuilder sb = new StringBuilder();
		bool didOreLevelUpscale = false;

		foreach (var foundOre in codeToFoundOre) {
			string oreCode = foundOre.Key;
			int empiricalAmount = foundOre.Value.Count;


			var reading = new OreReading();
			reading.PartsPerThousand = (double)empiricalAmount / chunkBlocks * 1000;

			DepositVariant variant = ppws.depositsByCode[oreCode];
			var generator = variant.GeneratorInst;

			double? totalFactor = CalculatorManager.GetPercentile(generator, variant, empiricalAmount);

			if (totalFactor == null) {
				var debugStr = $"[BetterEr Prospecting] Found no predefined calculator for {generator.GetType()}, using default factor";
				Logger.Debug(debugStr);
				if (config.DebugMode) {
					serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, debugStr, EnumChatType.Notification);
				}
				// Fallback to generic factor. Not entirely accurate but we can't leave it empty either ( we can but that would be evil )

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
	private static List<DelayedMessage> addMiscReadings(IWorldAccessor world, IServerPlayer sp, PropickReading readings, BlockPos pos) {
		var delayedMessages = new List<DelayedMessage>();

		// Hydrate Or Diedrate
		// Mimic getProbe and density prospecting 
		// Desperately waiting for insanityGod to update the code
		var a = AppDomain.CurrentDomain.GetAssemblies();
		if (AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "HydrateOrDiedrate") is Assembly assembly) {
			var configType = AccessTools.TypeByName("HydrateOrDiedrate.Config.ModConfig");
			var configInstance = AccessTools.Property(configType, "Instance")?.GetValue(null);
			var groundWater = AccessTools.Property(configInstance.GetType(), "GroundWater")?.GetValue(configInstance);

			bool showAquiferProspectingDataOnMap = AccessTools.Property(groundWater.GetType(), "ShowAquiferProspectingDataOnMap")?.GetValue(groundWater) as bool? ?? true;
			bool aquiferDataOnProspectingNodeMode = AccessTools.Property(groundWater.GetType(), "AquiferDataOnProspectingNodeMode")?.GetValue(groundWater) as bool? ?? false;


			var Aquifermanager = assembly.GetType("HydrateOrDiedrate.Aquifer.AquiferManager");
			if (Aquifermanager == null)
				Logger.Error("[BetterEr Prospecting] Hydrate Or Diedrate found but couldn't retrieve AquiferManager");

			var GetAquiferChunkDataChunkPos = Aquifermanager.GetMethod("GetAquiferChunkData", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(IWorldAccessor), typeof(FastVec3i), typeof(ILogger) }, null);
			var GetAquiferChunkDataBlockPos = Aquifermanager.GetMethod("GetAquiferChunkData", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(IWorldAccessor), typeof(BlockPos), typeof(ILogger) }, null);

			if (GetAquiferChunkDataChunkPos == null || GetAquiferChunkDataBlockPos == null)
				Logger.Error("[BetterEr Prospecting] Hydrate Or Diedrate found but couldn't retrieve GetAquiferChunkData");


			var aquiferData = GetAquiferChunkDataBlockPos.Invoke(null, new object[] { world, pos, world.Logger });

			if (aquiferData != null) {

				var dataProp = aquiferData.GetType().GetProperty("Data", BindingFlags.Public | BindingFlags.Instance);
				var dataObj = dataProp?.GetValue(aquiferData);

				var isSalty = (bool)dataObj.GetType().GetProperty("IsSalty").GetValue(dataObj);
				var rating = (int)dataObj.GetType().GetProperty("AquiferRating").GetValue(dataObj);


				if (showAquiferProspectingDataOnMap) {

					readings.OreReadings["$aquifer$"] = new OreReading() {
						DepositCode = isSalty ? "salty" : "fresh",
						PartsPerThousand = rating,
						TotalFactor = 0.0000001
					};
				}

				if (!aquiferDataOnProspectingNodeMode) {
					// This technically should happen after prospecting, but will be here for now

					int chunkX = pos.X / GlobalConstants.ChunkSize;
					int chunkY = pos.Y / GlobalConstants.ChunkSize;
					int chunkZ = pos.Z / GlobalConstants.ChunkSize;

					string GetAquiferDescription(bool isSalty, int rating, double worldHeight, double posY) {
						string aquiferType = isSalty ? Lang.Get("hydrateordiedrate:aquifer-salt") : Lang.Get("hydrateordiedrate:aquifer-fresh");
						return rating switch {
							<= 10 => Lang.Get("hydrateordiedrate:aquifer-none-detected"),
							<= 15 => Lang.Get("hydrateordiedrate:aquifer-very-poor", aquiferType),
							<= 20 => Lang.Get("hydrateordiedrate:aquifer-poor", aquiferType),
							<= 40 => Lang.Get("hydrateordiedrate:aquifer-light", aquiferType),
							<= 60 => Lang.Get("hydrateordiedrate:aquifer-moderate", aquiferType),
							_ => Lang.Get("hydrateordiedrate:aquifer-heavy", aquiferType)
						};
					}


					string GetDirectionHint(int dx, int dy, int dz) {
						string horizontal = "";
						string verticalHor = "";

						if (dz < 0)
							verticalHor = Lang.Get("hydrateordiedrate:direction-north");
						else if (dz > 0)
							verticalHor = Lang.Get("hydrateordiedrate:direction-south");

						if (dx > 0)
							horizontal = Lang.Get("hydrateordiedrate:direction-east");
						else if (dx < 0)
							horizontal = Lang.Get("hydrateordiedrate:direction-west");

						string horizontalPart = !string.IsNullOrEmpty(verticalHor) && !string.IsNullOrEmpty(horizontal)
							? verticalHor + "-" + horizontal
							: !string.IsNullOrEmpty(verticalHor) ? verticalHor : horizontal;

						string verticalDepth = "";
						if (dy > 0)
							verticalDepth = Lang.Get("hydrateordiedrate:direction-above");
						else if (dy < 0)
							verticalDepth = Lang.Get("hydrateordiedrate:direction-below");
						if (!string.IsNullOrEmpty(horizontalPart) && !string.IsNullOrEmpty(verticalDepth))
							return horizontalPart + " " + Lang.Get("hydrateordiedrate:direction-and") + " " + verticalDepth;
						else if (!string.IsNullOrEmpty(horizontalPart))
							return horizontalPart;
						else if (!string.IsNullOrEmpty(verticalDepth))
							return verticalDepth;
						else
							return Lang.Get("hydrateordiedrate:direction-here");
					}


					int currentRating = rating;
					string aquiferInfo = currentRating == 0 ? Lang.Get("hydrateordiedrate:aquifer-none") : GetAquiferDescription(isSalty, currentRating, world.BlockAccessor.MapSizeY, pos.Y);

					int radius = (int)AccessTools.Property(groundWater.GetType(), "ProspectingRadius")?.GetValue(groundWater);
					int bestRating = currentRating;
					FastVec3i bestChunk = new(chunkX, chunkY, chunkZ);

					for (int dx = -radius; dx <= radius; dx++)
						for (int dy = -radius; dy <= radius; dy++)
							for (int dz = -radius; dz <= radius; dz++) {
								if (dx == 0 && dy == 0 && dz == 0)
									continue;


								FastVec3i checkChunk = new(chunkX + dx, chunkY + dy, chunkZ + dz);

								var aquiferToCheckChunkData = GetAquiferChunkDataChunkPos.Invoke(null, new object[] { world, checkChunk, world.Logger });
								// I LOVE REFLECTION NULL CHECKS THEY ARE SO FUN
								if (aquiferToCheckChunkData != null) {
									var checkDataProp = aquiferToCheckChunkData.GetType().GetProperty("Data", BindingFlags.Public | BindingFlags.Instance);
									var checkAquiferData = checkDataProp?.GetValue(aquiferToCheckChunkData);
									if (checkAquiferData != null) {
										var ratingProp = checkAquiferData.GetType().GetProperty("AquiferRating", BindingFlags.Public | BindingFlags.Instance);
										if (ratingProp != null) {
											int checkRating = (int)ratingProp.GetValue(checkAquiferData);

											if (checkRating > bestRating) {
												bestRating = checkRating;
												bestChunk = checkChunk;
											}
										}
									}
								}
							}

					if (bestRating > currentRating) {
						int dxDir = bestChunk.X - chunkX;
						int dyDir = bestChunk.Y - chunkY;
						int dzDir = bestChunk.Z - chunkZ;

						string directionHint = GetDirectionHint(dxDir, dyDir, dzDir);
						aquiferInfo += Lang.Get("hydrateordiedrate:aquifer-direction", directionHint);
					}
					delayedMessages.Add(new DelayedMessage(aquiferInfo));
				}
			} else if (aquiferDataOnProspectingNodeMode) {
				delayedMessages.Add(new DelayedMessage(Lang.Get("hydrateordiedrate:aquifer-no-data")));
			}

		}

		return delayedMessages;
	}



	internal class DelayedMessage {
		public int chatGroup;
		public string message;
		public EnumChatType ChatType;

		internal DelayedMessage(int chatGroup, string message, EnumChatType chatType) {
			this.chatGroup = chatGroup;
			this.message = message;
			ChatType = chatType;
		}
		internal DelayedMessage(string message) {
			chatGroup = GlobalConstants.InfoLogChatGroup;
			this.message = message;
			ChatType = EnumChatType.Notification;
		}

		public void Send(IServerPlayer sp) {
			sp.SendMessage(chatGroup, message, ChatType);
		}
	}
	private bool isPropickable(Block block) {
		return block?.Attributes?["propickable"].AsBool(false) == true;
	}
	public override SkillItem[] GetToolModes(ItemSlot slot, IClientPlayer forPlayer, BlockSelection blockSel) {
		var capi = api as ICoreClientAPI;

		foreach (var modeSkill in toolModes) {
			Mode modeEnum = (Mode)Enum.Parse(typeof(Mode), modeSkill.Code.Path, true);
			var data = modeData[modeEnum];

			if (data.Texture == null) {
				data.Texture = capi.Gui.LoadSvgWithPadding(data.Asset, 48, 48, 5, ColorUtil.WhiteArgb);
			}

			modeSkill.WithIcon(capi, data.Texture);
			modeSkill.TexturePremultipliedAlpha = false;
		}

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
	private static string getHandbookLinkOrName(IWorldAccessor world, IServerPlayer serverPlayer, string key) {
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

	private void DebounceReload(Action action) {
		if (_reloadDebounce == null) {
			_reloadDebounce = new Timer(1000);
			_reloadDebounce.AutoReset = false;
			_reloadDebounce.Elapsed += (s, e) => action();
		}

		_reloadDebounce.Stop();
		_reloadDebounce.Start();
	}

	public override void OnUnloaded(ICoreAPI api) {
		foreach (var item in modeData?.Values) { item?.Skill?.Dispose(); }
		base.OnUnloaded(api);
	}

}
