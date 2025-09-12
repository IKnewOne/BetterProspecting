using System.Collections.Generic;
using BetterErProspecting.Item.Data;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using Vintagestory.ServerMods;

namespace BetterErProspecting;
public interface IGeneratorPercentileProvider {
	/// <summary>
	/// Registers a percentile calculator for a generator type.
	/// Signature: (generator, variant, empiricalValue) => percentile
	/// </summary>
	void RegisterCalculator<TGenerator>(System.Func<TGenerator, DepositVariant, int, double> calculator) where TGenerator : DepositGeneratorBase;
}


public interface IRealBlocksReadingsProvider {

	/// <summary>
	/// Generates readings for a chunk sized area based on existing ores. Use this if you want to have prospecting-related processed returning more real values.
	/// </summary>
	/// <param name="sapi"></param>
	/// <param name="serverPlayer"></param>
	/// <param name="blockSel">Prospecting center</param>
	/// <param name="readings"></param>
	/// <param name="delayedMessages">List of notifications</param>
	/// <returns></returns>
	bool ProbeBlockDensitySearch(ICoreServerAPI sapi, IServerPlayer serverPlayer, BlockSelection blockSel, out PropickReading readings, ref List<DelayedMessage> delayedMessages);
}