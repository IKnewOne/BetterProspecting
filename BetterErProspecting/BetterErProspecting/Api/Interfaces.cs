using System;
using Vintagestory.ServerMods;

namespace BetterErProspecting;
public interface IGeneratorPercentileProvider {
	/// <summary>
	/// Registers a percentile calculator for a generator type.
	/// Signature: (generator, variant, empiricalValue, sampled radius) => percentile
	/// Sampled radius is needed for normalization if we ever sample > 1 chunk. If we sample a ~ chunk ( 32x32 blocks ), we provide 32/2 = 16 sampled radius
	/// Calculated per chunk
	/// </summary>
	void RegisterCalculator<TGenerator>(Func<TGenerator, DepositVariant, int, int, double> calculator) where TGenerator : DepositGeneratorBase;
}