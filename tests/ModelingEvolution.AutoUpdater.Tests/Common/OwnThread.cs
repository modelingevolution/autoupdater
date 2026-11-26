namespace ModelingEvolution.AutoUpdater.Tests.Common
{
    /// <summary>
    /// Runs code that must block its thread (a Dispose that waits for its commands, a crash handler, a single-threaded context)
    /// on a thread of its own, and lets the test await it.
    /// </summary>
    /// <remarks>
    /// xUnit runs the tests themselves on the thread pool. On a 2-core CI runner the pool starts with 2 threads and adds more only
    /// slowly, so a test parked on a pool thread holds one of the threads every other test needs for its timers, continuations and
    /// renders - and those tests then fail their time bounds. A blocking call under test belongs here, never on a pool thread.
    /// </remarks>
    internal static class OwnThread
    {
        public static Task RunAsync(Action body)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            new Thread(() =>
            {
                try
                {
                    body();
                    done.SetResult();
                }
                catch (Exception ex)
                {
                    done.SetException(ex);
                }
            }) { IsBackground = true, Name = "test-own-thread" }.Start();
            return done.Task;
        }
    }
}
