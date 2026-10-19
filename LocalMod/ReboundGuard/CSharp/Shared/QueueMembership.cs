using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ReboundGuard
{
    // Membership only. The original FIFO still owns order and all data.
    // The queue's mutation version detects same-count replacement as well as growth.
    public sealed class QueueMembership<T>
    {
        // Counts preserve membership when duplicate entries are dequeued. A set
        // alone forces an O(n) rebuild after every dequeue/query pair.
        private readonly Dictionary<T, int> members = new Dictionary<T, int>();
        private int nullCount;
        private Queue<T> source;
        private int version;
        private bool valid;
        private readonly Func<Queue<T>,int> readVersion;
        public QueueMembership(Func<Queue<T>,int> readVersion) { this.readVersion = readVersion; }
        public void Reset() { source = null; members.Clear(); nullCount = 0; valid = false; }
        public bool Contains(Queue<T> queue, T value)
        {
            int current = readVersion(queue);
            if (!valid || source != queue || current != version)
            {
                members.Clear();
                nullCount = 0;
                // An interrupted rebuild must never leave a trusted partial map.
                valid = false;
                foreach (T item in queue) { Add(item); }
                source = queue;
                version = current;
                valid = true;
            }
            return value is null ? nullCount != 0 : members.ContainsKey(value);
        }
        public void Enqueue(Queue<T> queue, T value)
        {
            int before = readVersion(queue);
            queue.Enqueue(value);
            if (valid && source == queue && version == before)
            {
                valid = false;
                Add(value);
                version = readVersion(queue);
                valid = true;
            }
            else { valid = false; }
        }
        public T Dequeue(Queue<T> queue)
        {
            int before = readVersion(queue);
            T value = queue.Dequeue();
            bool tracked = valid && source == queue && version == before;
            valid = false;
            if (tracked)
            {
                if (value is null) nullCount--;
                else
                {
                    ref int count = ref CollectionsMarshal.GetValueRefOrNullRef(members, value);
                    if (Unsafe.IsNullRef(ref count)) return value; // Leave invalid; rebuild on next lookup.
                    if (count == 1) members.Remove(value);
                    else count--;
                }
                version = readVersion(queue);
                valid = true;
            }
            return value;
        }
        private void Add(T value)
        {
            // Dictionary rejects null keys, whereas Queue.Contains and HashSet
            // support null (including nullable value types with no value).
            if (value is null) { nullCount++; return; }
            ref int count = ref CollectionsMarshal.GetValueRefOrAddDefault(members, value, out _);
            count++;
        }
    }

    // Owned by one thread. Entries follow the lifetime of each native queue;
    // their value may reference its key without keeping that key alive (ephemeron).
    // Release the hot pair at every send boundary, including exceptional exits.
    public sealed class QueueMembershipCache<T>
    {
        private readonly ConditionalWeakTable<Queue<T>, QueueMembership<T>> entries =
            new ConditionalWeakTable<Queue<T>, QueueMembership<T>>();
        private readonly ConditionalWeakTable<Queue<T>, QueueMembership<T>>.CreateValueCallback create;
        private Queue<T> activeQueue;
        private QueueMembership<T> activeIndex;

        public QueueMembershipCache(Func<Queue<T>, int> readVersion)
        {
            create = queue => new QueueMembership<T>(readVersion);
        }
        public QueueMembership<T> Get(Queue<T> queue)
        {
            if (activeQueue != queue)
            {
                activeIndex = entries.GetValue(queue, create);
                activeQueue = queue;
            }
            return activeIndex;
        }
        public void Finish() { activeQueue = null; activeIndex = null; }
        public void Clear() { Finish(); entries.Clear(); }
    }
}
