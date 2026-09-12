using System.Collections.Generic;

namespace BestAutoSort.Core;

internal sealed class BoundedScanQueue<T> where T : class
{
	private readonly int _capacity;

	private readonly LinkedList<T> _queue = new LinkedList<T>();

	private readonly Dictionary<T, LinkedListNode<T>> _nodes = new Dictionary<T, LinkedListNode<T>>();

	internal int Count => _nodes.Count;

	internal BoundedScanQueue(int capacity)
	{
		_capacity = capacity;
	}

	internal bool Enqueue(T value)
	{
		if (_nodes.ContainsKey(value) || _nodes.Count >= _capacity)
		{
			return false;
		}
		_nodes.Add(value, _queue.AddLast(value));
		return true;
	}

	internal T? Dequeue()
	{
		if (_queue.First == null)
		{
			return null;
		}
		T value = _queue.First.Value;
		Remove(value);
		return value;
	}

	internal void Remove(T value)
	{
		if (_nodes.TryGetValue(value, out LinkedListNode<T> value2))
		{
			_nodes.Remove(value);
			_queue.Remove(value2);
		}
	}

	internal void Clear()
	{
		_nodes.Clear();
		_queue.Clear();
	}
}
