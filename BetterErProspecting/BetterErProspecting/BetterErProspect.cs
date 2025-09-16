using System;
using System.Linq;
using BetterErProspecting.Config;
using BetterErProspecting.Helper;
using BetterErProspecting.Item;
using ConfigLib;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.ServerMods;

namespace BetterErProspecting;

// I swear i won't change modsystem name anymore
public class BetterErProspect : Vintagestory.API.Common.ModSystem, IGeneratorPercentileProvider {
	public static ILogger Logger { get; private set; }
	public static ICoreAPI Api { get; private set; }
	public static Harmony harmony { get; private set; }

	public static event Action ReloadTools;

	/// <summary>
	/// Registers a percentile calculation method for a generator type. Making sure it's above vanilla's detector 0.025 is your job if you want that
	/// </summary>
	public void RegisterCalculator<TGenerator>(System.Func<TGenerator, DepositVariant, int, double> calculator) where TGenerator : DepositGeneratorBase {
		CalculatorManager.GeneratorToPercentileCalculator[typeof(TGenerator)] = (genBase, variant, empirical) => calculator((TGenerator)genBase, variant, empirical);
	}
	public override void Start(ICoreAPI api) {
		api.Logger.Debug("[BetterErProspecting] Starting...");
		base.Start(api);

		harmony = new Harmony(Mod.Info.ModID);
		Api = api;
		Logger = Mod.Logger;

		try {
			ModConfig.Instance = api.LoadModConfig<ModConfig>(ModConfig.ConfigName) ?? new ModConfig();
			api.StoreModConfig(ModConfig.Instance, ModConfig.ConfigName);
		} catch (Exception) { ModConfig.Instance = new ModConfig(); }

		if (api.ModLoader.IsModEnabled("configlib")) {
			SubscribeToConfigChange(api);
		}

		PatchUnpatch();

		RegisterCalculator<DiscDepositGenerator>((dGen, variant, empiricalValue) => DiscDistributionCalculator.getPercentileOfEmpiricalValue(dGen, variant, empiricalValue));
		api.RegisterItemClass("ItemBetterErProspectingPick", typeof(ItemBetterErProspectingPick));
	}


	private void SubscribeToConfigChange(ICoreAPI api) {
		ConfigLibModSystem system = api.ModLoader.GetModSystem<ConfigLibModSystem>();

		system.SettingChanged += (domain, config, setting) => {
			if (domain != "bettererprospecting")
				return;

			setting.AssignSettingValue(ModConfig.Instance);

			string[] settingsToolReload = [nameof(ModConfig.EnableDensityMode), nameof(ModConfig.NewDensityMode), nameof(ModConfig.AddBoreHoleMode), nameof(ModConfig.AddStoneMode), nameof(ModConfig.AddProximityMode)];
			string[] settingsPatch = [nameof(ModConfig.NewDensityMode)];

			if (settingsToolReload.Contains(setting.YamlCode)) {
				ReloadTools?.Invoke();
			}

			if (settingsPatch.Contains(setting.YamlCode)) {
				PatchUnpatch();
			}


		};
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

	public enum PatchCategory {
		NewDensityMode
	}
}

