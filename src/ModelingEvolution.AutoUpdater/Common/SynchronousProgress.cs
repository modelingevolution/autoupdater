using System;

namespace ModelingEvolution.AutoUpdater.Common
{
    /// <summary>
    /// <see cref="IProgress{T}"/> that invokes the handler on the reporting thread, in order.
    /// Unlike <see cref="Progress{T}"/> it never posts to a synchronization context or the thread pool,
    /// so consecutive reports cannot overtake each other.
    /// </summary>
    public sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public SynchronousProgress(Action<T> handler)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        public void Report(T value) => _handler(value);
    }
}
