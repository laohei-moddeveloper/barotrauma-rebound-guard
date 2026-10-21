using System;

namespace ReboundGuard
{
    // Exact invalidation stamp for a fixed set of bucket keys, provided EVERY
    // insertion/mutation gets a globally newer revision. Deleting the bucket
    // with a non-maximum revision must also invalidate: hence PresentCount.
    // Recreated/replaced buckets receive a new revision; no tombstones needed.
    // Structural ordering changes are checked separately by the caller.
    public struct BucketStamp : IEquatable<BucketStamp>
    {
        private long newest;
        private int presentCount;
        public void Include(long revision) { newest = Math.Max(newest, revision); presentCount++; }
        public bool Equals(BucketStamp other) => newest == other.newest && presentCount == other.presentCount;
        public override bool Equals(object other) => other is BucketStamp stamp && Equals(stamp);
        public override int GetHashCode() => HashCode.Combine(newest, presentCount);
    }
}
