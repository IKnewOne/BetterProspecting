﻿using Vintagestory.API.Common.CommandAbbr;

namespace BetterErProspecting.Prospecting;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BetterErProspecting.Config;
using BetterErProspecting.Item;
using BetterErProspecting.Item.Data;
using HydrateOrDiedrate;
using HydrateOrDiedrate.Wells.Aquifer;
using HydrateOrDiedrate.Wells.Aquifer.ModData;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using Vintagestory.ServerMods;

public class ProspectingSystem : ModSystem {

	public static ModConfig config => ModConfig.Instance;
	private static ILogger logger => BetterErProspect.Logger;
	private bool isReprospecting = false;
	private ICoreServerAPI sapi;

	public override void StartServerSide(ICoreServerAPI api) {
		base.StartServerSide(api);
		sapi = api;

		api.ChatCommands.GetOrCreate("reprospect")
		.RequiresPrivilege(Privilege.controlserver)
		.WithDesc("Regenerates prospecting data for all players ( including offline ). Optionally only for one player. Expensive operation.")
		.WithExamples("/reprospect", "/reprospect KnewOne")
		.WithArgs(new OnlinePlayerArgParser("player", api, isMandatoryArg: false))
		.HandleWith(Reprospect);
	}

	private TextCommandResult Reprospect(TextCommandCallingArgs args) {
		if (isReprospecting) {
			return TextCommandResult.Error("Please wait before the previous command ends");
		}

		// Background
		Task.Run(() => { ReprospectTask(args); });

		return TextCommandResult.Success("[BetterEr Prospect] Began reprospecting");
	}
	private readonly ConcurrentDictionary<(int, int), Task> chunkLoadTasks = new();

	private Task EnsureChunkLoaded(int cx, int cz) {
		return chunkLoadTasks.GetOrAdd((cx, cz), _ => {
			var tcs = new TaskCompletionSource();
			var chunk = sapi.WorldManager.GetMapChunk(cx, cz);
			if (chunk != null) {
				tcs.SetResult();
				return tcs.Task;
			}

			var opts = new ChunkLoadOptions();
			opts.OnLoaded += () => tcs.TrySetResult();
			sapi.WorldManager.LoadChunkColumnPriority(cx, cz, opts);
			return tcs.Task;
		});
	}

	// This might create lag or memory issues. Need more feedback on large world/many players
	private async Task ReprospectTask(TextCommandCallingArgs args) {
		var caller = args.Caller.Player as IServerPlayer;
		var targetPlayer = args.Parsers[0].GetValue() as IServerPlayer;
		int countSucc = 0;
		int countUnload = 0;

		try {
			logger.Notification("[BetterEr Prospecting] Reprospecting started by {0} on player? {1}",
				caller == null ? "console" : caller.PlayerName,
				targetPlayer == null ? "all" : targetPlayer);

			var world = sapi.World;
			var msom = sapi.ModLoader.GetModSystem<ModSystemOreMap>();
			var oml = sapi.ModLoader.GetModSystem<WorldMapManager>().MapLayers.FirstOrDefault(ml => ml is OreMapLayer) as OreMapLayer;

			if (isReprospecting)
				return;
			isReprospecting = true;
			chunkLoadTasks.Clear(); // Reruns

			// Inject offline players
			var allPlayers = world.AllPlayers;
			foreach (var player in allPlayers) { oml.getOrLoadReadings(player); }

			foreach (var kvp in oml.PropickReadingsByPlayer) {
				var player = world.PlayerByUid(kvp.Key) as IServerPlayer;
				var readings = kvp.Value;

				// Step 1: Collect all unique chunks for this player's readings
				var chunksToLoad = new HashSet<(int cx, int cz)>();
				foreach (var reading in readings) {
					int bx = reading.Position.XInt;
					int bz = reading.Position.ZInt;

					foreach (var dx in new[] { -32, 0, 32 })
						foreach (var dz in new[] { -32, 0, 32 }) {
							int cx = (bx + dx) / GlobalConstants.ChunkSize;
							int cz = (bz + dz) / GlobalConstants.ChunkSize;
							chunksToLoad.Add((cx, cz));
						}
				}

				// Step 2: Ensure all chunks are loaded in parallel
				await Task.WhenAll(chunksToLoad.Select(c => EnsureChunkLoaded(c.cx, c.cz)));

				// Step 3: Process readings in parallel
				var updatedReadings = await Task.WhenAll(readings.Select(async reading => {
					var readingBlock = reading.Position.AsBlockPos;
					var readingChunk = sapi.WorldManager.GetChunk(readingBlock);

					if (readingChunk == null) {
						Interlocked.Increment(ref countUnload);
						return reading;
					}

					generateReadigs(sapi, player, readingBlock, GenerateBlockData(sapi, readingBlock), out PropickReading newReading);
					Interlocked.Increment(ref countSucc);
					return newReading;
				}));

				// Step 4: Replace readings safely
				if (updatedReadings.Length > 0) {
					kvp.Value.Clear();
					kvp.Value.AddRange(updatedReadings);
				}
			}

			// Step 5: Serialize per player in parallel
			var savegame = sapi.WorldManager.SaveGame;
			var serializeTasks = oml.PropickReadingsByPlayer.Select(val => Task.Run(() => {
				using var ms = new FastMemoryStream();
				savegame.StoreData("oreMapMarkers-" + val.Key, SerializerUtil.Serialize(val.Value, ms));
			})).ToArray();

			await Task.WhenAll(serializeTasks);

			// Step 6: Clear offline players
			var onlineUids = sapi.World.AllOnlinePlayers.Select(p => p.PlayerUID).ToHashSet();
			oml.PropickReadingsByPlayer.RemoveAllByKey(uid => onlineUids.Contains(uid));

			logger.Notification("[BetterEr Prospecting] Reprospecting finished");
		} catch (Exception ex) {
			logger.Error("[BetterEr Prospecting] Error during reprospecting: {0}", ex);
		} finally {
			isReprospecting = false;
			if (caller != null) {
				caller.SendMessage(GlobalConstants.AllChatGroups,
					$"[BetterEr Prospecting] Finished reprospecting. Changed {countSucc} readings. Kept unchanged {countUnload} unloaded chunk readings",
					EnumChatType.Notification);
			}
		}
	}


	public static Dictionary<string, int> GenerateBlockData(ICoreServerAPI api, BlockPos blockPos, List<DelayedMessage> delayedMessages = null) {
		delayedMessages ??= new List<DelayedMessage>();

		int radius = ItemBetterErProspectingPick.densityRadius;

		int mapHeight = api.World.BlockAccessor.GetTerrainMapheightAt(blockPos);
		string[] knownBlacklistedCodes = ["flint", "quartz"];

		var ppws = ObjectCacheUtil.TryGet<ProPickWorkSpace>(api, "propickworkspace");

		Dictionary<string, int> codeToFoundCount = new();
		HashSet<string> nopageVariant = new HashSet<string>();
		var depositKeys = new HashSet<string>(ppws.depositsByCode.Keys);

		var blockCache = new Dictionary<string, string>();

		api.World.BlockAccessor.WalkBlocks(new BlockPos(blockPos.X - radius, mapHeight, blockPos.Z - radius), new BlockPos(blockPos.X + radius, 0, blockPos.Z + radius),
			(Block walkBlock, int x, int y, int z) => {
				if (walkBlock.Variant == null)
					return;

				string key;

				bool isOre = ItemBetterErProspectingPick.IsOre(walkBlock, blockCache, out string fullKey, out key);
				bool isRock = !isOre && ItemBetterErProspectingPick.IsRock(walkBlock, blockCache, out fullKey, out key);

				if (isOre || isRock) {
					if (knownBlacklistedCodes.Contains(key))
						return;

					if (depositKeys.Contains(key)) {
						codeToFoundCount[key] = codeToFoundCount.GetValueOrDefault(key, 0) + 1;
					} else if (isOre) {
						nopageVariant.Add(key);
					}
				}
			});

		if (nopageVariant.Count > 0) {
			delayedMessages.Add(new DelayedMessage(Lang.Get("bettererprospecting:debug-bad-ppws-key", String.Join(", ", nopageVariant))));
			delayedMessages.Add(new DelayedMessage(Lang.Get("bettererprospecting:debug-bad-ppws-key-expected", String.Join(", ", ppws.depositsByCode.Keys))));
		}

		return codeToFoundCount;
	}

	// Small generator type instead of full text
	private static string FormatGeneratorType(Type generatorType) {
		string fullName = generatorType.ToString();
		string[] parts = fullName.Split('.');
		if (parts.Length >= 2) {
			string corePackage = parts[0];
			string generatorName = parts[parts.Length - 1];
			return $"{corePackage}...{generatorName}";
		}
		return fullName;
	}
	public static bool generateReadigs(ICoreServerAPI sapi, IServerPlayer serverPlayer, BlockPos blockPos, Dictionary<string, int> codeToFoundOre, out PropickReading readings, List<DelayedMessage> delayedMessages = null) {
		delayedMessages ??= new List<DelayedMessage>();

		var world = sapi.World;
		LCGRandom Rnd = new LCGRandom(sapi.World.Seed);
		DepositVariant[] deposits = sapi.ModLoader.GetModSystem<GenDeposits>()?.Deposits;
		ProPickWorkSpace ppws = ObjectCacheUtil.TryGet<ProPickWorkSpace>(world.Api, "propickworkspace");
		if (deposits == null) {
			readings = null;
			return false;

		}

		int radius = ItemBetterErProspectingPick.densityRadius;

		int mapHeight = world.BlockAccessor.GetTerrainMapheightAt(blockPos);
		int zoneDiameter = 2 * radius;
		int zoneBlocks = zoneDiameter * zoneDiameter * mapHeight;

		var pos = blockPos;
		readings = new PropickReading();
		readings.Position = blockPos.ToVec3d();
		StringBuilder sb = new StringBuilder();

		StringBuilder tracerVis = new StringBuilder();
		StringBuilder poorVis = new StringBuilder();

		bool didOreLevelUpscale = false;

		foreach (var foundOre in codeToFoundOre) {
			string oreCode = foundOre.Key;
			int empiricalAmount = foundOre.Value;


			var reading = new OreReading();
			reading.PartsPerThousand = (double)empiricalAmount / zoneBlocks * 1000;

			DepositVariant variant = ppws.depositsByCode[oreCode];
			var generator = variant.GeneratorInst;

			double? totalFactor = CalculatorManager.GetPercentile(generator, variant, empiricalAmount, radius);
			bool isNoGeneratorOre = totalFactor == null;

			if (totalFactor == null) {
				sb.Append($"[BetterEr Prospecting] Found no predefined calculator for {FormatGeneratorType(generator.GetType())} for ore {oreCode}, using default generator");

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

			double initialFactor = (double)totalFactor;
			bool wasUplifted = false;
			string upliftReason = "";

			if (config.UpliftTraceOres) {
				// Check for poor uplift first (higher priority)
				if (totalFactor < 0.15 && (config.UpliftAllToPoor || (isNoGeneratorOre && config.UpliftToPoorNoGeneratorFound))) {
					totalFactor = 0.15;
					didOreLevelUpscale = true;
					wasUplifted = true;
					upliftReason = config.UpliftAllToPoor ? "P-All" : "P-NoGen";

					if (poorVis.Length == 0) {
						poorVis.Append($"[BetterEr Prospecting] Uplifted to poor: {initialFactor:0.####} -> {totalFactor:0.####} for {oreCode} ({upliftReason})");
					} else {
						poorVis.Append($", {initialFactor:0.####} -> {totalFactor:0.####} for {oreCode} ({upliftReason})");
					}
				}
				// Only uplift to trace if not already uplifted to poor and below mention threshold
				else if (!wasUplifted && totalFactor <= PropickReading.MentionThreshold) {
					totalFactor = PropickReading.MentionThreshold + 1e-6;
					didOreLevelUpscale = true;
					wasUplifted = true;
					upliftReason = "T";

					if (tracerVis.Length == 0) {
						tracerVis.Append($"[BetterEr Prospecting] Uplifted to trace: {initialFactor:0.####} -> {totalFactor:0.####} for {oreCode}");
					} else {
						tracerVis.Append($", {initialFactor:0.####} -> {totalFactor:0.####} for {oreCode}");
					}
				}
			}

			reading.TotalFactor = (double)totalFactor;
			readings.OreReadings[oreCode] = reading;
		}


		if (config.DebugMode && (sb.Length > 0 || tracerVis.Length > 0 || poorVis.Length > 0)) {
			if (sb.Length > 0) {
				delayedMessages.Add(new DelayedMessage(sb.ToString()));
			}
			if (poorVis.Length > 0) {
				delayedMessages.Add(new DelayedMessage(poorVis.ToString()));
			}
			if (tracerVis.Length > 0) {
				delayedMessages.Add(new DelayedMessage(tracerVis.ToString()));
			}
		}


		addMiscReadings(sapi, serverPlayer, readings, blockPos, delayedMessages);

		return true;
	}

	#region Compat
	private static void addMiscReadings(ICoreServerAPI sapi, IServerPlayer serverPlayer, PropickReading readings, BlockPos blockPos, List<DelayedMessage> delayedMessages) {
		// Hydrate Or Diedrate
		if (sapi.ModLoader.IsModEnabled("hydrateordiedrate")) {
			if (isHoDCompat(sapi, delayedMessages)) {
				hydrateOrDiedrate(sapi, readings, blockPos, delayedMessages);
			}
		}
	}

	private static void hydrateOrDiedrate(ICoreServerAPI sapi, PropickReading readings, BlockPos pos, List<DelayedMessage> delayedMessages) {
		var world = sapi.World;
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

	private static bool isHoDCompat(ICoreServerAPI sapi, List<DelayedMessage> delayedMessages) {
		HydrateOrDiedrateModSystem system = sapi.ModLoader.GetModSystem<HydrateOrDiedrateModSystem>();
		// Latest bump to 2.2.13 due to modified namespace
		// Blame HoD
		var minVer = new Version("2.2.13");
		if (new Version(system.Mod.Info.Version) < minVer) {
			delayedMessages.Add(new DelayedMessage($"[BetterEr Prospecting] Please update HydrateOrDietrade to at least {minVer.ToString()} for aquifer support"));
			return false;
		}

		return true;
	}
	#endregion

	public override void Dispose() {
		sapi = null;
		base.Dispose();
	}
}
