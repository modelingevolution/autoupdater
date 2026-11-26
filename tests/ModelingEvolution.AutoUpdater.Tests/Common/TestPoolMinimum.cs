using System.Runtime.CompilerServices;
using Xunit;

namespace ModelingEvolution.AutoUpdater.Tests.Common
{
    /// <summary>
    /// Gives the code under test the thread-pool minimum it has in production.
    /// </summary>
    /// <remarks>
    /// The test runner (xUnit through Reqnroll's collection runner) runs each test collection as a thread-pool work item and keeps
    /// that thread for the whole collection, up to maxParallelThreads collections at once. On a 2-core CI runner that is the pool's
    /// whole minimum (the processor count), so the timers, continuations and renders of the code under test queued behind the
    /// pool's slow starvation injection and missed their time bounds. Adding back exactly the threads the runner keeps restores the
    /// production minimum - never more.
    /// </remarks>
    internal static class TestPoolMinimum
    {
        /// <summary>Pool threads the runner keeps for collections: maxParallelThreads from the xUnit config, 1 when not parallel.</summary>
        internal static int RunnerThreads { get; } = ReadRunnerThreads();

        /// <summary>The worker minimum this assembly asks for: the production minimum plus the runner's threads.</summary>
        internal static int WorkerMinimum => Environment.ProcessorCount + RunnerThreads;

        [ModuleInitializer]
        internal static void Reserve()
        {
            if (RunnerThreads <= 0) return; // "unlimited": the runner keeps no fixed number of threads, so there is nothing to give back
            ThreadPool.GetMinThreads(out var worker, out var completionPort);
            if (worker < WorkerMinimum)
            {
                ThreadPool.SetMinThreads(WorkerMinimum, completionPort);
            }
        }

        private static int ReadRunnerThreads()
        {
            // xUnit's own reader: xunit.runner.json / <assembly>.json next to the assembly, with xUnit's defaults.
            var config = ConfigReader.Load(typeof(TestPoolMinimum).Assembly.Location);
            return config.ParallelizeTestCollectionsOrDefault ? config.MaxParallelThreadsOrDefault : 1;
        }
    }
}
