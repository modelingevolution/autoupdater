using System;
using System.Collections.Immutable;
using System.Threading.Tasks;

namespace ModelingEvolution.AutoUpdater.Common
{

    /// <summary>
    /// A set of event subscriptions that can be disposed together
    /// </summary>
    public class SubscriptionSet : IDisposable
    {
        private readonly ImmutableArray<IEventSubscription> _subscriptions;

        public SubscriptionSet(ImmutableArray<IEventSubscription> subscriptions)
        {
            _subscriptions = subscriptions;
        }

        /// <summary>
        /// Creates a subscription set from a single subscription
        /// </summary>
        public static implicit operator SubscriptionSet(EventSubscription subscription)
        {
            return new SubscriptionSet(ImmutableArray.Create<IEventSubscription>(subscription));
        }

        /// <summary>
        /// Adds a subscription to the set
        /// </summary>
        public static SubscriptionSet operator +(SubscriptionSet set, IEventSubscription subscription)
        {
            return new SubscriptionSet(set._subscriptions.Add(subscription));
        }


        /// <summary>
        /// Combines two subscription sets
        /// </summary>
        public static SubscriptionSet operator +(SubscriptionSet left, SubscriptionSet right)
        {
            return new SubscriptionSet(left._subscriptions.AddRange(right._subscriptions));
        }

        public void Dispose()
        {
            foreach (var subscription in _subscriptions)
            {
                subscription?.Dispose();
            }
        }
    }

    /// <summary>
    /// Event hub interface for publishing and subscribing to events
    /// </summary>
    public interface IEventHub
    {
        /// <summary>
        /// Publishes an event to all subscribers
        /// </summary>
        /// <typeparam name="TEvent">Type of the event to publish</typeparam>
        /// <param name="eventData">The event data to publish</param>
        Task PublishAsync<TEvent>(TEvent eventData) where TEvent : class;

        /// <summary>
        /// Subscribes to events of a specific type
        /// </summary>
        /// <typeparam name="TEvent">Type of the event to subscribe to</typeparam>
        /// <param name="handler">The event handler to execute when the event is published</param>
        /// <returns>A subscription handle that can be used to unsubscribe</returns>
        IEventSubscription Subscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : class;

        /// <summary>
        /// Subscribes to events of a specific type with a synchronous handler
        /// </summary>
        /// <typeparam name="TEvent">Type of the event to subscribe to</typeparam>
        /// <param name="handler">The synchronous event handler to execute when the event is published</param>
        /// <returns>A subscription handle that can be used to unsubscribe</returns>
        IEventSubscription Subscribe<TEvent>(Action<TEvent> handler) where TEvent : class;

        /// <summary>
        /// Unsubscribes from events using a subscription handle
        /// </summary>
        /// <param name="subscription">The subscription handle returned from Subscribe</param>
        void Unsubscribe(IEventSubscription subscription);
    }
}