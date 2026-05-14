namespace GxMcp.Gateway
{
    /// <summary>
    /// Decides whether a lifecycle build should use the synchronous fast-path (blocks,
    /// returns the build result inline) or the asynchronous path (fires Task.Run, returns
    /// a job_id immediately).
    ///
    /// Keeping the logic in a static method makes it trivially unit-testable without
    /// spinning up a full gateway stack.
    /// </summary>
    public static class BuildPathSelector
    {
        /// <summary>
        /// Returns <c>true</c> when the build should run synchronously (short build).
        /// </summary>
        /// <param name="estimatedSeconds">Caller-supplied estimate (or heuristic default).</param>
        /// <param name="thresholdSeconds">
        /// Maximum estimated duration that still qualifies for the sync fast-path.
        /// Comes from <see cref="ServerConfig.BuildSyncThresholdSeconds"/>.
        /// </param>
        public static bool UseSync(int estimatedSeconds, int thresholdSeconds)
            => estimatedSeconds < thresholdSeconds;
    }
}
