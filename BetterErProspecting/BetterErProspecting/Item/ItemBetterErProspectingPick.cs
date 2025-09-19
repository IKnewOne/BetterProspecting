using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BetterErProspecting.Item.Data;
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
using ModConfig = BetterErProspecting.Config.ModConfig;

namespace BetterErProspecting.Item;
public partial class ItemBetterErProspectingPick : ItemProspectingPick {
	public static ILogger Logger => BetterErProspect.Logger;
	ICoreServerAPI sapi;
	SkillItem[] toolModes;
	public enum Mode {
		density,
		node,
		proximity,
		stone,
		borehole
	}

	private Dictionary<Mode, ModeData> modeDataStorage = new Dictionary<Mode, ModeData>() {
		{ Mode.density, new ModeData(Mode.density, "textures/icons/heatmap.svg") },
		{ Mode.node, new ModeData(Mode.node, "textures/icons/rocks.svg") },
		{ Mode.proximity, new ModeData(Mode.proximity, "textures/icons/worldmap/spiral.svg") },
		{ Mode.stone, new ModeData(Mode.stone, "textures/icons/probe_stone.svg", "bettererprospecting") },
		{ Mode.borehole, new ModeData(Mode.borehole, "textures/icons/probe_borehole.svg", "bettererprospecting") }
	};

	public static ModConfig config => ModConfig.Instance;
	public override void OnLoaded(ICoreAPI api) {
		sapi = api as ICoreServerAPI;

		GenerateToolModes(api);

		var deposits = api.ModLoader.GetModSystem<GenDeposits>()?.Deposits;
		BetterErProspect.ReloadTools += () => {
			GenerateToolModes(api);
		};

		base.OnLoaded(api);
	}

	private void GenerateToolModes(ICoreAPI api) {
		ObjectCacheUtil.Delete(api, "proPickToolModes");
		toolModes = ObjectCacheUtil.GetOrCreate(api, "proPickToolModes", () => {
			List<SkillItem> modes = new List<SkillItem>();

			// Density mode (two possible names, same SkillItem)
			if (config.EnableDensityMode) {
				if (config.NewDensityMode) {
					modeDataStorage[Mode.density].Skill.Name = Lang.Get("Density Search Mode (Long range, chance based search)"); // This is a real vanilla lang string lmao
				} else {
					modeDataStorage[Mode.density].Skill.Name = Lang.Get("bettererprospecting:density-block-based");
				}
				modes.Add(modeDataStorage[Mode.density].Skill);
			}


			// Node mode
			if (api.World.Config.GetAsInt("propickNodeSearchRadius") > 0) {
				modeDataStorage[Mode.node].Skill.Name = Lang.Get("bettererprospecting:node");
				modes.Add(modeDataStorage[Mode.node].Skill);
			}

			// Proximity mode
			if (config.AddProximityMode) {
				modeDataStorage[Mode.proximity].Skill.Name = Lang.Get("bettererprospecting:proximity");
				modes.Add(modeDataStorage[Mode.proximity].Skill);
			}

			// Borehole mode
			if (config.AddBoreHoleMode) {
				modeDataStorage[Mode.borehole].Skill.Name = Lang.Get("bettererprospecting:borehole");
				modes.Add(modeDataStorage[Mode.borehole].Skill);
			}

			// Stone mode
			if (config.AddStoneMode) {
				modeDataStorage[Mode.stone].Skill.Name = Lang.Get("bettererprospecting:stone");
				modes.Add(modeDataStorage[Mode.stone].Skill);
			}

			return modes.ToArray();
		});
	}

	public override bool OnBlockBrokenWith(IWorldAccessor world, Entity byEntity, ItemSlot itemslot, BlockSelection blockSel, float dropQuantityMultiplier = 1) {
		IPlayer byPlayer = (byEntity as EntityPlayer).Player;
		int tm = GetToolMode(itemslot, byPlayer, blockSel);
		int damage = 1;

		if (tm >= 0) {
			SkillItem skillItem = toolModes[tm];
			Mode toolMode = (Mode)Enum.Parse(typeof(Mode), skillItem.Code.Path, true);


			switch (toolMode) {
				case Mode.density:
					if (config.NewDensityMode) {
						ProbeBlockDensityMode(world, byPlayer, itemslot, blockSel, ref damage);
					} else {
						ProbeVanillaDensity(world, byEntity, itemslot, blockSel, ref damage);
					}
					break;
				case Mode.node:
					base.ProbeBlockNodeMode(world, byEntity, itemslot, blockSel, api.World.Config.GetAsInt("propickNodeSearchRadius"));
					break;
				case Mode.proximity:
					ProbeProximity(world, byPlayer, itemslot, blockSel, ref damage);
					break;
				case Mode.stone:
					ProbeStone(world, byPlayer, itemslot, blockSel, ref damage);
					break;
				case Mode.borehole:
					ProbeBorehole(world, byPlayer, itemslot, blockSel, ref damage);
					break;
			}

		} else {
			// All modes disabled
			// Propickn't
			world.BlockAccessor.GetBlock(blockSel.Position).OnBlockBroken(world, blockSel.Position, byPlayer, 1);
		}


		if (DamagedBy != null && DamagedBy.Contains(EnumItemDamageSource.BlockBreaking)) {
			DamageItem(world, byEntity, itemslot, damage);
		}

		return true;
	}

	// Handle oneshot here too
	protected virtual void ProbeVanillaDensity(IWorldAccessor world, Entity byEntity, ItemSlot itemslot, BlockSelection blockSel, ref int damage) {
		if (config.OneShotDensity) {
			damage = 3;
			IPlayer byPlayer = (byEntity as EntityPlayer).Player;

			if (!breakIsPropickable(world, byPlayer, blockSel, ref damage))
				return;

			if (byPlayer is IServerPlayer severPlayer)
				base.PrintProbeResults(world, severPlayer, itemslot, blockSel.Position);

		} else {
			base.ProbeBlockDensityMode(world, byEntity, itemslot, blockSel);
		}
	}

	// Modded Density amount-based search. Square with chunkSize radius around current block. Whole mapheight
	protected virtual void ProbeBlockDensityMode(IWorldAccessor world, IPlayer byPlayer, ItemSlot itemslot, BlockSelection blockSel, ref int damage) {
		damage = 3;

		if (!breakIsPropickable(world, byPlayer, blockSel, ref damage))
			return;

		IServerPlayer serverPlayer = byPlayer as IServerPlayer;
		if (serverPlayer == null)
			return;

		int radius = GlobalConstants.ChunkSize;
		int mapHeight = world.BlockAccessor.GetTerrainMapheightAt(blockSel.Position);
		int chunkBlocks = radius * radius * mapHeight;

		ProPickWorkSpace ppws = ObjectCacheUtil.TryGet<ProPickWorkSpace>(api, "propickworkspace");

		BlockPos searchedBlock = blockSel.Position.Copy();

		List<DelayedMessage> delayedMessages = new List<DelayedMessage>();

		var cache = new Dictionary<string, string>();
		Dictionary<string, int> codeToFoundCount = new();
		HashSet<string> nopageVariant = new HashSet<string>();
		var depositKeys = new HashSet<string>(ppws.depositsByCode.Keys);

		string[] knownBlacklistedCodes = ["flint", "quartz"];

		api.World.BlockAccessor.WalkBlocks(new BlockPos(searchedBlock.X - radius, mapHeight, searchedBlock.Z - radius), new BlockPos(searchedBlock.X + radius, 0, searchedBlock.Z + radius),
			(Block walkBlock, int x, int y, int z) => {
				if (walkBlock.Variant == null)
					return;

				string key;

				bool isOre = IsOre(walkBlock, cache, out string fullKey, out key);
				bool isRock = !isOre && IsRock(walkBlock, cache, out fullKey, out key);

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

		if (!generateReadigs(sapi, serverPlayer, ppws, blockSel.Position, codeToFoundCount, out PropickReading readings, delayedMessages))
			return;

		addMiscReadings(sapi, serverPlayer, readings, blockSel.Position, delayedMessages);

		var textResults = readings.ToHumanReadable(serverPlayer.LanguageCode, ppws.pageCodes);
		serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, textResults, EnumChatType.Notification);

		if (config.DebugMode) {
			delayedMessages.ForEach(msg => msg.Send(serverPlayer));
		}

		world.Api.ModLoader.GetModSystem<ModSystemOreMap>()?.DidProbe(readings, serverPlayer);

	}

	// Radius-based search
	protected virtual void ProbeProximity(IWorldAccessor world, IPlayer byPlayer, ItemSlot itemslot, BlockSelection blockSel, ref int damage) {
		damage = config.ProximityDmg;
		int radius = config.ProximitySearchRadius;

		if (!breakIsPropickable(world, byPlayer, blockSel, ref damage))
			return;

		IServerPlayer serverPlayer = byPlayer as IServerPlayer;
		if (serverPlayer == null)
			return;

		BlockPos pos = blockSel.Position.Copy();
		int closestOre = -1;
		var cache = new Dictionary<string, string>();

		api.World.BlockAccessor.WalkBlocks(pos.AddCopy(radius, radius, radius), pos.AddCopy(-radius, -radius, -radius),
			(walkBlock, x, y, z) => {
				if (IsOre(walkBlock, cache, out string key)) {
					var distanceTo = (int)Math.Round(pos.DistanceTo(x, y, z));

					if (closestOre == -1 || closestOre > distanceTo) {
						closestOre = distanceTo;
					}
				}
			});

		if (closestOre != -1) {
			serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, Lang.GetL(serverPlayer.LanguageCode, "bettererprospecting:closest-ore-is", closestOre), EnumChatType.Notification);
		} else {
			serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, Lang.GetL(serverPlayer.LanguageCode, "bettererprospecting:closest-ore-not-found", radius), EnumChatType.Notification);
		}
	}

	// Radius-based search
	protected virtual void ProbeStone(IWorldAccessor world, IPlayer byPlayer, ItemSlot itemslot, BlockSelection blockSel, ref int damage) {
		damage = config.StoneDmg;
		int walkRadius = config.StoneSearchRadius;

		if (!breakIsPropickable(world, byPlayer, blockSel, ref damage))
			return;

		IServerPlayer serverPlayer = byPlayer as IServerPlayer;
		if (serverPlayer == null)
			return;

		StringBuilder sb = new StringBuilder();

		sb.AppendLine(Lang.GetL(serverPlayer.LanguageCode, "bettererprospecting:area-sample", walkRadius));

		// Int is either distance or count
		Dictionary<string, int> rockInfo = new Dictionary<string, int>();

		BlockPos blockPos = blockSel.Position.Copy();
		var cache = new Dictionary<string, string>();
		api.World.BlockAccessor.WalkBlocks(blockPos.AddCopy(walkRadius, walkRadius, walkRadius), blockPos.AddCopy(-walkRadius, -walkRadius, -walkRadius),
			(walkBlock, x, y, z) => {
				if (IsRock(walkBlock, cache, out string key)) {

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
			serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, Lang.GetL(serverPlayer.LanguageCode, "bettererprospecting:no-rocks-near"), EnumChatType.Notification);
			return;
		}

		sb.AppendLine(Lang.GetL(serverPlayer.LanguageCode, "bettererprospecting:found-rocks"));


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
				sb.AppendLine(Lang.GetL(serverPlayer.LanguageCode, "bettererprospecting:stone-mode-blocks-away", itemLink, amount));
			}

		}
		serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, sb.ToString(), EnumChatType.Notification);
	}

	// Line-based search
	protected virtual void ProbeBorehole(IWorldAccessor world, IPlayer byPlayer, ItemSlot itemslot, BlockSelection blockSel, ref int damage) {
		damage = config.BoreholeDmg;

		if (!breakIsPropickable(world, byPlayer, blockSel, ref damage))
			return;


		IServerPlayer serverPlayer = byPlayer as IServerPlayer;
		if (serverPlayer == null)
			return;

		BlockFacing face = blockSel.Face;


		if (!config.BoreholeScansOre && !config.BoreholeScansStone) {
			serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, Lang.GetL(serverPlayer.LanguageCode, "bettererprospecting:borehole-no-filter"), EnumChatType.Notification);
			return;
		}

		// It's MY mod. And i get to decide what's important for immersion:tm:
		if (face != BlockFacing.UP) {
			serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, Lang.GetL(serverPlayer.LanguageCode, "bettererprospecting:borehole-sample-upside"), EnumChatType.Notification);
			return;
		}

		StringBuilder sb = new StringBuilder();
		ProPickWorkSpace ppws = ObjectCacheUtil.TryGet<ProPickWorkSpace>(api, "propickworkspace");

		sb.Append(Lang.GetL(serverPlayer.LanguageCode, "bettererprospecting:borehole-sample-taken"));

		// Need to hold unique insertion order. OrderedHashSet where art thou ?
		var blockKeys = new OrderedDictionary<string, string>();
		var cache = new Dictionary<string, string>();

		BlockPos blockPos = blockSel.Position.Copy();

		//Walk unreliable
		while (blockPos.Y > 0) {
			Block sBlock = api.World.BlockAccessor.GetBlock(blockPos);

			if (config.BoreholeScansOre && IsOre(sBlock, cache, out string fullKey, out string oreKey)) {
				var oreHandbook = ppws.depositsByCode.GetValueOrDefault(oreKey, null)?.HandbookPageCode;
				blockKeys.TryAdd(fullKey, oreHandbook);
			} else
			if (config.BoreholeScansStone && IsRock(sBlock, cache, out fullKey, out string rockKey)) {
				blockKeys.TryAdd(fullKey, null);
			}

			blockPos.Y--;
		}

		if (blockKeys.Count == 0) {
			sb.AppendLine();
			sb.AppendLine(Lang.GetL(serverPlayer.LanguageCode, "bettererprospecting:borehole-not-found"));
		} else {
			sb.AppendLine(Lang.GetL(serverPlayer.LanguageCode, "bettererprospecting:borehole-found"));
			var linkedNames = blockKeys.Select(kv => getHandbookLinkOrName(world, serverPlayer, kv.Key, handbookUrl: blockKeys[kv.Key])).ToList();
			sb.AppendLine(string.Join(", ", linkedNames));
		}

		serverPlayer.SendMessage(GlobalConstants.InfoLogChatGroup, sb.ToString(), EnumChatType.Notification);

		return;
	}

	public override SkillItem[] GetToolModes(ItemSlot slot, IClientPlayer forPlayer, BlockSelection blockSel) {
		var capi = api as ICoreClientAPI;

		foreach (var modeSkill in toolModes) {
			Mode modeEnum = (Mode)Enum.Parse(typeof(Mode), modeSkill.Code.Path, true);
			var data = modeDataStorage[modeEnum];

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

		remain = (remain + remainingResistance) / 2.2f;
		return remain;
	}
	public override void OnUnloaded(ICoreAPI api) {
		foreach (var item in modeDataStorage?.Values) { item?.Skill?.Dispose(); }
		base.OnUnloaded(api);
	}

}
