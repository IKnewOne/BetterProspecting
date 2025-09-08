using System;
using Vintagestory.ServerMods;

namespace BetterErProspecting;
public interface IGeneratorPercentileProvider {
	/// <summary>
	/// Registers a percentile calculator for a generator type.
	/// Signature: (generator, variant, empiricalValue) => percentile
	/// </summary>
	void RegisterCalculator<TGenerator>(Func<TGenerator, DepositVariant, int, double> calculator) where TGenerator : DepositGeneratorBase;
}