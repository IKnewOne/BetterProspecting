using Vintagestory.API.Client;
using Vintagestory.API.Common;
using static BetterErProspecting.Item.ItemBetterErProspectingPick;

namespace BetterErProspecting.Item.Data;

public class ModeData {
	public AssetLocation Asset;
	public LoadedTexture Texture;
	public SkillItem Skill;

	public ModeData(Mode mode, string assetPath, string domain = null) {
		Asset = domain == null ? new AssetLocation(assetPath) : new AssetLocation(domain, assetPath);
		Skill = new SkillItem { Code = new AssetLocation(mode.ToString()) };
	}
}

