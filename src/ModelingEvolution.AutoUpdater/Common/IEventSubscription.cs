using System;
using System.Collections.Immutable;

namespace ModelingEvolution.AutoUpdater.Common
{
    /// <summary>
    /// Represents a subscription to an event
    /// </summary>
    public interface IEventSubscription : IDisposable
    {
        /// <summary>
        /// Unique identifier for the subscription
        /// </summary>
        Guid Id { get; }

        /// <summary>
        /// The type of event this subscription handles
        /// </summary>
        Type EventType { get; }

        /// <summary>
        /// Whether this subscription is still active
        /// </summary>
        bool IsActive { get; }

        /// <summary>
        /// Unsubscribes from the event
        /// </summary>
        void Unsubscribe();
        public static SubscriptionSet operator +(IEventSubscription left, IEventSubscription right)
        {
            return new SubscriptionSet(ImmutableArray.Create<IEventSubscription>(left, right));
        }
    }
}