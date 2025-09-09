namespace BetterErProspecting.Config;

public class ModConfig {
	public static string ConfigName = "BetterErProspecting.json";
	public static ModConfig Instance { get; set; } = new ModConfig();

	public bool NewDensityMode = true;
	public int NewDensityDmg = 3;

	public bool AddProximityMode = true;
	public int ProximitySearchRadius = 5;
	public int ProximityDmg = 2;

	public bool AddStoneMode = true;
	public bool StonePercentSearch = true;
	public int StoneSearchRadius = 64;
	public int StoneDmg = 4;

	public bool AddBoreHoleMode = true;
	public int BoreholeDmg = 2;
	public bool BoreholeScansOre = true;
	public bool BoreholeScansStone = false;

	public bool AlwaysAddTraceOres = false;
	public bool AddToPoorOres = false;
	public bool DebugMode = false;
}