namespace ReboundGuard
{
    public static class Settings
    {
        // Approximate correction stays off. See README for all switches needed
        // to compare the optimized build with diagnostics-only behavior.
        public static bool ApplyExperimentalCorrection = false;
        public static bool Diagnostics = true;
        public static bool SaveDiagnostics = true;
        public static bool OptimizePositionQueue = true;
        public static bool SkipIrrelevantContainerSearches = true;
        public static bool UseNearbyItemIndex = true;
        public static bool BackgroundNearbyIndexBuild = true;
        public static int MinimumItemsForNearbyIndex = 256;
        public static bool ParallelNearbyTagAudit = true;
        public static int MinimumItemsForParallelTagAudit = 8192;
        public static bool ParallelBodyMaintenance = true;
        public static bool ParallelConstraintPreparation = true;
        public static int MinimumContactsForParallelPreparation = 1024;
        public static bool DetailedProfiling = true;
        public static bool NativeKnownModHotPaths = true;
        public static bool FixExitDisposalOrder = true;
        public static bool OptimizeLuaBridge = true;
        public static bool RecycleLuaPatchModules = true;
        public static bool FixReferenceEnumerationLifetime = true;
        // Research path: preserves per-body constraint order, but is NOT enabled
        // until realistic scene profiling establishes an end-to-end benefit.
        public static bool ParallelConstraintSolver = false;
        public static int MinimumConstraintsForParallel = 2048;
        public static int MinimumConstraintBatchWidth = 128;
        public static int MinimumBodiesForParallel = 8192;
        public static int MaximumWorkers = 4;
        // Simulation units: vanilla uses 100 display units per simulation unit.
        public const float MaximumCorrection = 2.0f;
        public const float MaximumPreservedTravel = 3.0f;
    }
}
