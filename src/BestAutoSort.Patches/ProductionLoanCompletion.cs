using BestAutoSort.Runtime;

namespace BestAutoSort.Patches;

internal static class ProductionLoanCompletion
{
	internal static void Complete(ProductionItemLoan? loan)
	{
		loan?.ReturnIfPresent();
	}
}
