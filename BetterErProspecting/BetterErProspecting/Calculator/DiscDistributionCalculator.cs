using System;
using Vintagestory.API.MathTools;
using Vintagestory.ServerMods;

namespace BetterErProspecting.Helper;


// The guy that made this says it's fine, and i'm too dumb to do this myself
public static class DiscDistributionCalculator {
	/// <summary>
	/// Computes the percentile of an empirical value of seen blocks
	/// Uses log-normal approximation for efficiency.
	/// </summary>
	public static double getPercentileOfEmpiricalValue(
		DiscDepositGenerator dGen,
		DepositVariant variant,
		int empiricalValue
	) {

		// If only one is, its variance will disappear and math will be fine
		if (dGen.Radius.dist == EnumDistribution.DIRAC && dGen.Thickness.dist == EnumDistribution.DIRAC) {
			double exact = (dGen.Thickness.avg + dGen.Thickness.offset) * Math.Pow(dGen.Radius.avg + dGen.Radius.offset, 2) * Math.PI * variant.TriesPerChunk;

			if (empiricalValue < exact)
				return 0.15;// Per dirac this should be 0, but if we're here, it means we've found some ore
							// which means we need to display this info. 0.15 should correspond to "Poor"
							// so it will be set to this. It's 2 tiers below 0.5 which is 2 tiers below 1.0. Works out in the end
			if (empiricalValue > exact)
				return 1.0;
			return 0.5;
		}

		// --- Step 1: Compute mean & std of radius and thickness averages ---
		double radiusMean = dGen.Radius.avg + dGen.Radius.offset;
		double thicknessMean = dGen.Thickness.avg + dGen.Thickness.offset;

		double radiusStd = GetNatFloatStd(dGen.Radius);
		double thicknessStd = GetNatFloatStd(dGen.Thickness);

		// --- Step 2: Log-transform approximation ---
		// log(Y) = log(thicknessAvg) + 2*log(radiusAvg) + log(pi * triesPerChunk)
		double logEmpirical = Math.Log(empiricalValue + 0.5); // + 0.5 = continuity

		// Approximate mean & variance in log-space using delta method
		// Var[log(X)] ≈ (Std[X]/Mean[X])^2 for small std
		double logRadiusMean = Math.Log(radiusMean);
		double logThicknessMean = Math.Log(thicknessMean);

		double logRadiusVar = Math.Pow(radiusStd / radiusMean, 2);
		double logThicknessVar = Math.Pow(thicknessStd / thicknessMean, 2);

		double logMean = (logThicknessMean - 0.5 * logThicknessVar) + 2.0 * (logRadiusMean - 0.5 * logRadiusVar) + Math.Log(Math.PI * variant.TriesPerChunk);
		double logStd = Math.Sqrt(logThicknessVar + 4.0 * logRadiusVar);  // 2*log(radius) → factor squared

		// --- Step 3: Compute percentile from Gaussian CDF ---
		double z = (logEmpirical - logMean) / logStd;
		double percentile = StandardNormalCDF(z);

		return Math.Clamp(percentile, 0.0, 1.0);
	}

	/// <summary>
	/// Approximates the standard deviation of a NatFloat based on its distribution.
	/// </summary>
	private static double GetNatFloatStd(NatFloat nf) {
		switch (nf.dist) {
			case EnumDistribution.UNIFORM:
				return nf.var / Math.Sqrt(3); // Uniform [-var,var]
			case EnumDistribution.TRIANGLE:
				return nf.var / Math.Sqrt(6); // Triangle [-var,var]
			case EnumDistribution.GAUSSIAN:
				return nf.var / Math.Sqrt(3); // Approximate using Irwin-Hall (avg of 3 uniforms)
			case EnumDistribution.NARROWGAUSSIAN:
				return nf.var / Math.Sqrt(6); // Irwin-Hall avg of 6
			case EnumDistribution.VERYNARROWGAUSSIAN:
				return nf.var / Math.Sqrt(12); // Irwin-Hall avg of 12
			case EnumDistribution.INVEXP:
				return nf.var * 0.25; // Approximate from product of 2 uniforms
			case EnumDistribution.STRONGINVEXP:
				return nf.var * 0.15; // Approximate from product of 3 uniforms
			case EnumDistribution.STRONGERINVEXP:
				return nf.var * 0.1; // Approximate from product of 4 uniforms
			case EnumDistribution.DIRAC:
				return 0.0; // No variance
			default:
				throw new ArgumentOutOfRangeException("This distribution is not supported"); // Achievement unlocked: How did we get here ?
		}
	}

	/// <summary>
	/// Standard Normal CDF using error function.
	/// </summary>
	private static double StandardNormalCDF(double x) {
		return 0.5 * (1.0 + Erf(x / Math.Sqrt(2.0)));
	}

	/// <summary>
	/// Approximation of the error function
	/// </summary>
	private static double Erf(double x) {
		// Abramowitz and Stegun formula 7.1.26
		double t = 1.0 / (1.0 + 0.3275911 * Math.Abs(x));
		double tau = t * (0.254829592 + t * (-0.284496736 + t * (1.421413741 +
					t * (-1.453152027 + t * 1.061405429 * t))));
		double ans = 1.0 - tau * Math.Exp(-x * x);
		return x >= 0 ? ans : -ans;
	}
}
