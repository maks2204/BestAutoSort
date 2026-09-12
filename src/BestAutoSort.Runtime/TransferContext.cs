using System.Collections.Generic;
using UnityEngine;

namespace BestAutoSort.Runtime;

internal static class TransferContext
{
	internal static Container? Container { get; private set; }

	internal static List<TransferRecord>? Records { get; private set; }

	internal static bool Active
	{
		get
		{
			if ((Object)(object)Container != (Object)null)
			{
				return Records != null;
			}
			return false;
		}
	}

	internal static void Begin(Container container, List<TransferRecord> records)
	{
		Container = container;
		Records = records;
	}

	internal static void End()
	{
		Container = null;
		Records = null;
	}
}
