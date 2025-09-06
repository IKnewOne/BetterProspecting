using Vintagestory.ServerMods;

namespace BetterErProspecting.Interface;
public interface IGeneratorPercentileProvider {

	/// <summary>
	/// A generator method that returns the percentile that the provided ore count represents for the given deposit generator and variant.
	///                    This percentile should be irrespective of heatmap or rock column.
	/// </summary>
	public abstract double getPercentileOfEmpiricalValue(int empiricalValue, DepositVariant variant);
}
