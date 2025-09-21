using Vintagestory.API.Common.CommandAbbr;

namespace BetterErProspecting.Prospecting;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
	private Task ReprospectTask(TextCommandCallingArgs args) {
		var caller = args.Caller.Player as IServerPlayer;
		var targetPlayer = args.Parsers[0].GetValue() as IServerPlayer;
		try {
			logger.Notification("[BetterEr Prospecting] Reprospecting started by {0} on player? {1}", caller == null ? "console" : caller.PlayerName, targetPlayer == null ? "all" : targetPlayer);
			var world = sapi.World;

			var msom = sapi.ModLoader.GetModSystem<ModSystemOreMap>();
			var oml = sapi.ModLoader.GetModSystem<WorldMapManager>().MapLayers.FirstOrDefault(ml => ml is OreMapLayer) as OreMapLayer;


			// Inject offline players
			var allplayers = world.AllPlayers;
			foreach (var player in allplayers) { oml.getOrLoadReadings(player); }

			foreach (var kvp in oml.PropickReadingsByPlayer) {
				var player = world.PlayerByUid(kvp.Key) as IServerPlayer;
				var newReadings = new List<PropickReading>();

				foreach (var reading in kvp.Value) {
					var readingBlock = new BlockPos(reading.Position.XInt, reading.Position.YInt, reading.Position.ZInt);
					var blData = GenerateBlockData(sapi, readingBlock);
					generateReadigs(sapi, player, readingBlock, blData, out PropickReading newReading);
					newReadings.Add(newReading);
				}

				// We will probably likely lose a reading that a player did in the middle of this.
				// Too bad
				kvp.Value.Clear();
				newReadings.ForEach(reading => { msom.DidProbe(reading, player); });
			}


			foreach (var val in oml.PropickReadingsByPlayer) {
				ISaveGame savegame = sapi.WorldManager.SaveGame;
				using FastMemoryStream ms = new();
				savegame.StoreData("oreMapMarkers-" + val.Key, SerializerUtil.Serialize(val.Value, ms));
			}

			// Clear offline players from memory
			var onlineUids = sapi.World.AllOnlinePlayers.Select(p => p.PlayerUID).ToList();
			oml.PropickReadingsByPlayer.RemoveAllByKey(uid => onlineUids.Contains(uid));


			logger.Notification("[BetterEr Prospecting] Reprospecting finished");
		} catch (Exception ex) {
			logger.Error("[BetterEr Prospecting] Error during reprospecting: {0}", ex);
		} finally {
			isReprospecting = false;
			if (caller != null) {
				caller.SendMessage(GlobalConstants.AllChatGroups, "[BetterEr Prospecting] Finished reprospecting", EnumChatType.Notification);
			}
		}
		return Task.CompletedTask;
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
		StringBuilder factVisSb = new StringBuilder();
		bool didOreLevelUpscale = false;

		foreach (var foundOre in codeToFoundOre) {
			string oreCode = foundOre.Key;
			int empiricalAmount = foundOre.Value;


			var reading = new OreReading();
			reading.PartsPerThousand = (double)empiricalAmount / zoneBlocks * 1000;

			DepositVariant variant = ppws.depositsByCode[oreCode];
			var generator = variant.GeneratorInst;

			double? totalFactor = CalculatorManager.GetPercentile(generator, variant, empiricalAmount, radius);

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
				if (factVisSb.Length == 0) {
					factVisSb.Append($"[BetterEr Prospecting] Factor is below visibility: {totalFactor:0.####} for {oreCode}");
				} else {
					factVisSb.Append($", {totalFactor:0.####} for {oreCode}");
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


		if (config.DebugMode && (sb.Length > 0 || factVisSb.Length > 0)) {
			if (didOreLevelUpscale) {
				factVisSb.AppendLine();
				factVisSb.AppendLine(config.AddToPoorOres ? "Modified to poor level" : "Modified to trace level");
			}

			if (factVisSb.Length > 0) {
				delayedMessages.Add(new DelayedMessage(factVisSb.ToString()));
			}
			if (sb.Length > 0) {
				delayedMessages.Add(new DelayedMessage(sb.ToString()));
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
