namespace BetterErProspecting.Config;

public class ModConfig {
	public static string ConfigName = "BetterErProspecting.json";
	public static ModConfig Instance { get; set; } = new ModConfig();


	public bool EnableDensityMode = true;
	public bool OneShotDensity = false;

	public bool NewDensityMode = true;
	public int NewDensityDmg = 3;

	public bool LinearDensityScaling = true;
	public float OreDetectionMultiplier = 1.0f;
	public float OreCalculationDivider = 1.0f;
	public float TriesPerChunkScaleFactor = 0.70f;

	public bool AddProximityMode = true;
	public int ProximitySearchRadius = 5;
	public int ProximityDmg = 2;

	public bool UpliftTraceOres = false;
	public bool UpliftToPoorNoGeneratorFound = true;
	public bool UpliftAllToPoor = false;

	public bool AddStoneMode = true;
	public bool StonePercentSearch = true;
	public int StoneSearchRadius = 64;
	public int StoneDmg = 4;

	public bool AddBoreHoleMode = true;
	public int BoreholeRadius = 8;
	public int BoreholeDmg = 2;
	public bool BoreholeScansOre = true;
	public bool BoreholeScansStone = false;

	public bool DebugMode = false;
}
