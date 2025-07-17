using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ModelingEvolution.AutoUpdater.Common
{
    /// <summary>
    /// Simple in-memory event hub implementation
    /// </summary>
    public class EventHub : IEventHub
    {
        private readonly ConcurrentDictionary<Type, ConcurrentDictionary<Guid, EventSubscription>> _subscriptions;
        private readonly ILogger<EventHub> _logger;

        public EventHub(ILogger<EventHub> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _subscriptions = new ConcurrentDictionary<Type, ConcurrentDictionary<Guid, EventSubscription>>();
        }

        public async Task PublishAsync<TEvent>(TEvent eventData) where TEvent : class
        {
            if (eventData == null)
                throw new ArgumentNullException(nameof(eventData));

            var eventType = typeof(TEvent);
            
            _logger.LogDebug("Publishing event of type {EventType}", eventType.Name);

            if (!_subscriptions.TryGetValue(eventType, out var subscribers))
            {
                _logger.LogDebug("No subscribers found for event type {EventType}", eventType.Name);
                return;
            }

            var activeSubscriptions = subscribers.Values.Where(s => s.IsActive).ToList();
            
            if (!activeSubscriptions.Any())
            {
                _logger.LogDebug("No active subscribers found for event type {EventType}", eventType.Name);
                return;
            }

            _logger.LogDebug("Publishing event to {SubscriberCount} subscribers", activeSubscriptions.Count);

            // Execute all handlers in parallel
            var tasks = activeSubscriptions.Select(subscription => subscription.InvokeAsync(eventData));
            
            try
            {
                await Task.WhenAll(tasks);
                _logger.LogDebug("Event published successfully to all subscribers");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while publishing event of type {EventType}", eventType.Name);
                // Don't rethrow - we want to be resilient to handler failures
            }
        }

        public IEventSubscription Subscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : class
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            var eventType = typeof(TEvent);
            var subscriptionId = Guid.NewGuid();

            _logger.LogDebug("Creating subscription {SubscriptionId} for event type {EventType}", subscriptionId, eventType.Name);

            // Wrap the typed handler in an untyped handler
            Task UntypedHandler(object eventData) => handler((TEvent)eventData);

            var subscription = new EventSubscription(
                subscriptionId,
                eventType,
                UntypedHandler,
                () => RemoveSubscription(eventType, subscriptionId)
            );

            // Add subscription to the dictionary
            var typeSubscriptions = _subscriptions.GetOrAdd(eventType, _ => new ConcurrentDictionary<Guid, EventSubscription>());
            typeSubscriptions.TryAdd(subscriptionId, subscription);

            _logger.LogDebug("Subscription {SubscriptionId} created successfully for event type {EventType}", subscriptionId, eventType.Name);

            return subscription;
        }

        public IEventSubscription Subscribe<TEvent>(Action<TEvent> handler) where TEvent : class
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            // Convert synchronous handler to async
            return Subscribe<TEvent>(eventData =>
            {
                handler(eventData);
                return Task.CompletedTask;
            });
        }

        public void Unsubscribe(IEventSubscription subscription)
        {
            if (subscription == null)
                throw new ArgumentNullException(nameof(subscription));

            _logger.LogDebug("Unsubscribing subscription {SubscriptionId} for event type {EventType}", subscription.Id, subscription.EventType.Name);

            subscription.Unsubscribe();
        }

        private void RemoveSubscription(Type eventType, Guid subscriptionId)
        {
            if (_subscriptions.TryGetValue(eventType, out var typeSubscriptions))
            {
                typeSubscriptions.TryRemove(subscriptionId, out _);
                
                // Clean up empty type dictionaries
                if (typeSubscriptions.IsEmpty)
                {
                    _subscriptions.TryRemove(eventType, out _);
                }
            }

            _logger.LogDebug("Subscription {SubscriptionId} removed for event type {EventType}", subscriptionId, eventType.Name);
        }

        /// <summary>
        /// Gets the current number of active subscriptions for diagnostic purposes
        /// </summary>
        public int GetSubscriptionCount()
        {
            return _subscriptions.Values.Sum(typeSubscriptions => typeSubscriptions.Count);
        }

        /// <summary>
        /// Gets the number of active subscriptions for a specific event type
        /// </summary>
        public int GetSubscriptionCount<TEvent>() where TEvent : class
        {
            var eventType = typeof(TEvent);
            return _subscriptions.TryGetValue(eventType, out var typeSubscriptions) 
                ? typeSubscriptions.Count 
                : 0;
        }
    }
}