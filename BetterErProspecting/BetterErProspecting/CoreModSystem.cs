using System;
using BetterErProspecting.Config;
using BetterErProspecting.Helper;
using BetterErProspecting.Item;
using ConfigLib;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.ServerMods;
namespace BetterErProspecting;
public class CoreModSystem : ModSystem, IGeneratorPercentileProvider {
	public static ILogger Logger { get; private set; }
	public static ICoreAPI Api { get; private set; }
	public static Harmony harmony { get; private set; }

	public static event Action<ISetting> SettingChanged;

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
			ModConfig.Instance = api.LoadModConfig<ModConfig>(ModConfig.ConfigName);
			if (ModConfig.Instance == null) {
				ModConfig.Instance = new ModConfig();
				Logger.VerboseDebug("[BetterErProspecting] Config file not found, creating a new one...");
			}
			api.StoreModConfig(ModConfig.Instance, ModConfig.ConfigName);
		} catch (Exception e) {
			Logger.Error("[BetterErProspecting] Failed to load config, you probably made a typo: {0}", e);
			ModConfig.Instance = new ModConfig();
		}

		if (api.ModLoader.IsModEnabled("configlib")) {
			SubscribeToConfigChange(api);

			// Need something better
			SettingChanged += (setting) => {
				if (setting.YamlCode == nameof(ModConfig.NewDensityMode)) {
					PatchUnpatch();
				}
			};
		}

		PatchUnpatch();

		base.Start(api);

		RegisterCalculator<DiscDepositGenerator>((dGen, variant, empiricalValue) => DiscDistributionCalculator.getPercentileOfEmpiricalValue(dGen, variant, empiricalValue));
		api.RegisterItemClass("ItemBetterErProspectingPick", typeof(ItemBetterErProspectingPick));
	}


	private void SubscribeToConfigChange(ICoreAPI api) {
		ConfigLibModSystem system = api.ModLoader.GetModSystem<ConfigLibModSystem>();

		system.SettingChanged += (domain, config, setting) => {
			if (domain != "bettererprospecting")
				return;

			// Color is a bit fucked rn
			if (setting.AssignSettingValue(ModConfig.Instance) && setting.SettingType == ConfigSettingType.Color) {
				ModConfig.Instance = api.LoadModConfig<ModConfig>(ModConfig.ConfigName);
			}

			SettingChanged.Invoke(setting);
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

