using FluentAssertions;

namespace ModelingEvolution.AutoUpdater.Tests.Common
{
    public class TestPoolMinimumTests
    {
        [Fact]
        public void TheWorkerMinimum_IsTheProcessorCountPlusTheRunnersThreads()
        {
            ThreadPool.GetMinThreads(out var worker, out _);

            TestPoolMinimum.RunnerThreads.Should().BeGreaterThan(0, "this assembly runs collections in parallel on a bounded number of threads");
            TestPoolMinimum.WorkerMinimum.Should().Be(Environment.ProcessorCount + TestPoolMinimum.RunnerThreads);
            worker.Should().Be(TestPoolMinimum.WorkerMinimum, "the module initializer must have given the runner's threads back, and no more");
        }
    }
}
