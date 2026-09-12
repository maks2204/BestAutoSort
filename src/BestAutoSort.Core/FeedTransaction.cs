using System;

namespace BestAutoSort.Core;

public static class FeedTransaction
{
	public static bool Execute(Func<bool> withdraw, Action feed, Func<bool> fed, Action persist, Action restore)
	{
		try
		{
			if (!withdraw())
			{
				throw new InvalidOperationException("Food withdrawal did not complete.");
			}
			feed();
			if (!fed())
			{
				throw new InvalidOperationException("Creature feeding did not complete.");
			}
			persist();
			return true;
		}
		catch (Exception ex)
		{
			try
			{
				restore();
			}
			catch (Exception ex2)
			{
				throw new AggregateException("Food transaction recovery needs attention.", ex, ex2);
			}
			return false;
		}
	}
}
