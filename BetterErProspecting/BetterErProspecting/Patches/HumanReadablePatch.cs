using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.GameContent;
using static BetterErProspecting.ModSystem;

namespace BetterErProspecting.Patches;

[HarmonyPatch(typeof(PropickReading), nameof(PropickReading.ToHumanReadable))]
[HarmonyPatchCategory(nameof(PatchCategory))]
public static class PropickReadingPatch {
	static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
		var matcher = new CodeMatcher(instructions);

		// Try to match the ldstr "0.##"
		matcher.MatchStartForward(new CodeMatch(OpCodes.Ldstr, "0.##"));

		if (matcher.IsValid) {
			// Only replace if we actually found it
			matcher.SetInstruction(new CodeInstruction(OpCodes.Ldstr, "0.#####"));
		}

		return matcher.InstructionEnumeration();
	}
}