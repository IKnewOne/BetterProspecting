using System;
using System.Collections.Generic;
using BetterErProspecting.Config;
using BetterErProspecting.Helper;
using BetterErProspecting.Item;
using BetterErProspecting.Item.Data;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using Vintagestory.ServerMods;

namespace BetterErProspecting;

public class ModSystem : Vintagestory.API.Common.ModSystem, IGeneratorPercentileProvider, IRealBlocksReadingsProvider {
	public static ILogger Logger { get; private set; }
	public static ICoreAPI Api { get; private set; }
	public static Harmony harmony { get; private set; }

	/// <summary>
	/// Registers a percentile calculation method for a generator type. Making sure it's above vanilla's detector 0.025 is your job if you want that
	/// </summary>
	public void RegisterCalculator<TGenerator>(System.Func<TGenerator, DepositVariant, int, double> calculator) where TGenerator : DepositGeneratorBase {
		CalculatorManager.GeneratorToPercentileCalculator[typeof(TGenerator)] = (genBase, variant, empirical) => calculator((TGenerator)genBase, variant, empirical);
	}
	public override void Start(ICoreAPI api) {
		api.Logger.Debug("[BetterErProspecting] Starting...");

		harmony = new Harmony(Mod.Info.ModID);
		Api = api;
		Logger = Mod.Logger;

		try {
			ModConfig.Instance = api.LoadModConfig<ModConfig>(ModConfig.ConfigName) ?? new ModConfig();
			api.StoreModConfig(ModConfig.Instance, ModConfig.ConfigName);
		} catch (Exception) { ModConfig.Instance = new ModConfig(); }

		PatchUnpatch();

		base.Start(api);

		RegisterCalculator<DiscDepositGenerator>((dGen, variant, empiricalValue) => DiscDistributionCalculator.getPercentileOfEmpiricalValue(dGen, variant, empiricalValue));
		api.RegisterItemClass("ItemBetterErProspectingPick", typeof(ItemBetterErProspectingPick));
	}

	public override void Dispose() {
		harmony?.UnpatchAll(Mod.Info.ModID);
		ModConfig.Instance = null;
		harmony = null;
		Logger = null;
		Api = null;
		base.Dispose();
	}

	private void PatchUnpatch() {
		if (ModConfig.Instance.NewDensityMode) {
			harmony.PatchCategory(nameof(PatchCategory.NewDensityMode));
		} else {
			harmony.UnpatchCategory(nameof(PatchCategory.NewDensityMode));
		}
	}

	public bool ProbeBlockDensitySearch(ICoreServerAPI sapi, IServerPlayer serverPlayer, BlockSelection blockSel, out PropickReading readings, ref List<DelayedMessage> delayedMessages) {
		return ItemBetterErProspectingPick.ProbeBlockDensitySearch(sapi, serverPlayer, blockSel, out readings, ref delayedMessages);
	}


	public enum PatchCategory {
		NewDensityMode
	}
}

