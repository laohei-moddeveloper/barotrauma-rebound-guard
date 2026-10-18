using System;
using System.Collections.Generic;

namespace ReboundGuard
{
    // Membership only. The original FIFO still owns order and all data.
    // The queue's mutation version detects same-count replacement as well as growth.
    public sealed class QueueMembership<T>
    {
        private readonly HashSet<T> members = new HashSet<T>();
        private Queue<T> source;
        private int version;
        private bool valid;
        private readonly Func<Queue<T>,int> readVersion;
        public QueueMembership(Func<Queue<T>,int> readVersion) { this.readVersion = readVersion; }
        public void Reset() { source = null; members.Clear(); valid = false; }
        public bool Contains(Queue<T> queue, T value)
        {
            int current = readVersion(queue);
            if (!valid || source != queue || current != version)
            {
                members.Clear();
                foreach (T item in queue) { members.Add(item); }
                source = queue;
                version = current;
                valid = true;
            }
            return members.Contains(value);
        }
        public void Enqueue(Queue<T> queue, T value)
        {
            int before = readVersion(queue);
            queue.Enqueue(value);
            if (valid && source == queue && version == before)
            {
                members.Add(value);
                version = readVersion(queue);
            }
            else { valid = false; }
        }
        public T Dequeue(Queue<T> queue)
        {
            T value = queue.Dequeue();
            valid = false; // Rebuild if queried again; also handles duplicate entries.
            return value;
        }
    }
}
