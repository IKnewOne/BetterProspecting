using System;
using System.Collections.Generic;
using Vintagestory.ServerMods;

namespace BetterErProspecting;
public class CalculatorManager {

	public static readonly Dictionary<Type, Func<DepositGeneratorBase, DepositVariant, int, int, double>> GeneratorToPercentileCalculator = new();

	public static double? GetPercentile(DepositGeneratorBase generator, DepositVariant variant, int empirical, int sampledRadius) {
		Type type = generator.GetType();

		// Use parent's calculator if current type doesn't implement
		while (type != null) {
			if (GeneratorToPercentileCalculator.TryGetValue(type, out var calc))
				return calc(generator, variant, empirical, sampledRadius);

			type = type.BaseType;
		}

		return null;
	}
}
