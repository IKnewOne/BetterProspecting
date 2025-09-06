using System;
using HarmonyLib;
using Vintagestory.API.Common;

namespace BetterErProspecting;
public class BetterErProspectingModSystem : ModSystem {
	public static ILogger Logger { get; private set; }
	public static ICoreAPI Api { get; private set; }
	public static Harmony harmony { get; private set; }

	public enum PatchCategory {
		NewDensityMode
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

		if (ModConfig.Instance.NewDensityMode) {
			harmony.PatchCategory(nameof(PatchCategory.NewDensityMode));
		}

		base.Start(api);
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

}

