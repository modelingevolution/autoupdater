using System;
using System.Collections.Immutable;
using System.Threading.Tasks;

namespace ModelingEvolution.AutoUpdater.Common
{
    /// <summary>
    /// Internal implementation of event subscription
    /// </summary>
    public class EventSubscription : IEventSubscription
    {
        private readonly Func<object, Task> _handler;
        private readonly Action _unsubscribeAction;
        private bool _isDisposed = false;

        public EventSubscription(Guid id, Type eventType, Func<object, Task> handler, Action unsubscribeAction)
        {
            Id = id;
            EventType = eventType ?? throw new ArgumentNullException(nameof(eventType));
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _unsubscribeAction = unsubscribeAction ?? throw new ArgumentNullException(nameof(unsubscribeAction));
        }

        public Guid Id { get; }

        public Type EventType { get; }

        public bool IsActive => !_isDisposed;

        public async Task InvokeAsync(object eventData)
        {
            if (_isDisposed)
                return;

            try
            {
                await _handler(eventData);
            }
            catch
            {
                // Swallow exceptions from event handlers to prevent one handler from affecting others
                // In a production system, you might want to log these exceptions
            }
        }

        public void Unsubscribe()
        {
            if (!_isDisposed)
            {
                _unsubscribeAction();
                _isDisposed = true;
            }
        }

        public void Dispose()
        {
            Unsubscribe();
        }

        /// <summary>
        /// Creates a subscription set from two subscriptions
        /// </summary>
        public static SubscriptionSet operator +(EventSubscription left, IEventSubscription right)
        {
            return new SubscriptionSet(ImmutableArray.Create<IEventSubscription>(left, right));
        }
    }
}