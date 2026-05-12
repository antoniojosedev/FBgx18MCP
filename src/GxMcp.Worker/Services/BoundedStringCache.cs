using System;
using System.Collections.Generic;

namespace GxMcp.Worker.Services
{
    internal sealed class BoundedStringCache
    {
        private readonly int _capacity;
        private readonly Dictionary<string, Entry> _map;
        private readonly LinkedList<string> _lru = new LinkedList<string>();
        private readonly object _lock = new object();

        public BoundedStringCache(int capacity)
        {
            _capacity = Math.Max(1, capacity);
            _map = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        }

        public bool TryGetValue(string key, out string value)
        {
            value = null;
            if (string.IsNullOrEmpty(key)) return false;

            lock (_lock)
            {
                if (!_map.TryGetValue(key, out var entry)) return false;
                _lru.Remove(entry.Node);
                _lru.AddFirst(entry.Node);
                value = entry.Value;
                return true;
            }
        }

        public void TryAdd(string key, string value)
        {
            if (string.IsNullOrEmpty(key) || value == null) return;

            lock (_lock)
            {
                if (_map.TryGetValue(key, out var existing))
                {
                    existing.Value = value;
                    _lru.Remove(existing.Node);
                    _lru.AddFirst(existing.Node);
                    return;
                }

                while (_map.Count >= _capacity)
                {
                    var last = _lru.Last;
                    if (last == null) break;
                    _map.Remove(last.Value);
                    _lru.RemoveLast();
                }

                var node = new LinkedListNode<string>(key);
                _lru.AddFirst(node);
                _map[key] = new Entry { Value = value, Node = node };
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _map.Clear();
                _lru.Clear();
            }
        }

        private sealed class Entry
        {
            public string Value;
            public LinkedListNode<string> Node;
        }
    }
}
