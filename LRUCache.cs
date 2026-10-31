using System;
using System.Collections.Generic;

namespace BreakEternity
{
    /// <summary>
    /// A thread-safe least-recently-used cache with a fixed capacity. Used by <see cref="BigDouble.Parse(string)"/>
    /// so that parsing the same string repeatedly does not repeat the work.
    /// </summary>
    /// <remarks>
    /// Entries form a doubly linked list ordered from most recently used (first) to least recently used (last).
    /// When the cache is full, the node of the evicted entry is reused for the new entry, so a full cache does not
    /// allocate on insert.
    /// </remarks>
    public sealed class LRUCache<K, V>
    {
        private sealed class Node
        {
            public K Key;
            public V Value;
            public Node Prev;
            public Node Next;
        }

        private readonly Dictionary<K, Node> _map;
        private readonly object _sync = new object();
        private readonly int _maxSize;
        private Node _first;
        private Node _last;

        /// <param name="maxSize">
        /// The maximum number of entries. A value below 1 disables the cache (nothing is stored).
        /// </param>
        public LRUCache(int maxSize)
        {
            _maxSize = maxSize;
            _map = new Dictionary<K, Node>(maxSize > 0 ? maxSize : 0);
        }

        /// <summary>The maximum number of entries.</summary>
        public int MaxSize => _maxSize;

        /// <summary>The current number of entries.</summary>
        public int Count
        {
            get
            {
                lock (_sync)
                {
                    return _map.Count;
                }
            }
        }

        /// <summary>
        /// Looks up a key. Returns true and marks the entry as most recently used if it exists.
        /// </summary>
        public bool TryGetValue(K key, out V value)
        {
            if (key == null)
            {
                value = default;
                return false;
            }
            lock (_sync)
            {
                if (!_map.TryGetValue(key, out var node))
                {
                    value = default;
                    return false;
                }
                MoveToFront(node);
                value = node.Value;
                return true;
            }
        }

        /// <summary>
        /// Adds or replaces an entry and marks it as most recently used. Evicts the least recently used entry when
        /// the cache is full.
        /// </summary>
        public void Set(K key, V value)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }
            lock (_sync)
            {
                if (_maxSize < 1)
                {
                    return;
                }
                if (_map.TryGetValue(key, out var existing))
                {
                    existing.Value = value;
                    MoveToFront(existing);
                    return;
                }

                Node node;
                if (_map.Count >= _maxSize)
                {
                    // Reuse the least recently used node.
                    node = _last;
                    Unlink(node);
                    _map.Remove(node.Key);
                }
                else
                {
                    node = new Node();
                }
                node.Key = key;
                node.Value = value;
                LinkFirst(node);
                _map.Add(key, node);
            }
        }

        /// <summary>Removes an entry. Returns true if it existed.</summary>
        public bool Remove(K key)
        {
            if (key == null)
            {
                return false;
            }
            lock (_sync)
            {
                if (!_map.TryGetValue(key, out var node))
                {
                    return false;
                }
                Unlink(node);
                _map.Remove(key);
                return true;
            }
        }

        /// <summary>Removes all entries.</summary>
        public void Clear()
        {
            lock (_sync)
            {
                _map.Clear();
                _first = null;
                _last = null;
            }
        }

        /// <summary>Keys from most recently to least recently used (for tests and diagnostics).</summary>
        public List<K> KeysByRecency()
        {
            lock (_sync)
            {
                var keys = new List<K>(_map.Count);
                for (var node = _first; node != null; node = node.Next)
                {
                    keys.Add(node.Key);
                }
                return keys;
            }
        }

        /// <summary>Returns the value for key, or default(V) if it is not cached (API of the previous port).</summary>
        public V get(K key)
        {
            TryGetValue(key, out var value);
            return value;
        }

        /// <summary>Same as <see cref="Set"/> (API of the previous port; existing keys are now updated instead of throwing).</summary>
        public void set(K key, V value)
        {
            Set(key, value);
        }

        private void MoveToFront(Node node)
        {
            if (node == _first)
            {
                return;
            }
            Unlink(node);
            LinkFirst(node);
        }

        private void LinkFirst(Node node)
        {
            node.Prev = null;
            node.Next = _first;
            if (_first != null)
            {
                _first.Prev = node;
            }
            _first = node;
            if (_last == null)
            {
                _last = node;
            }
        }

        private void Unlink(Node node)
        {
            if (node.Prev != null)
            {
                node.Prev.Next = node.Next;
            }
            else
            {
                _first = node.Next;
            }
            if (node.Next != null)
            {
                node.Next.Prev = node.Prev;
            }
            else
            {
                _last = node.Prev;
            }
            node.Prev = null;
            node.Next = null;
        }
    }
}
