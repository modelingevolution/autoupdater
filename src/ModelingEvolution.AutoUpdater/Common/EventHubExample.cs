using Microsoft.Extensions.Logging;
using ModelingEvolution.AutoUpdater.Common.Events;
using System;
using System.Threading.Tasks;

namespace ModelingEvolution.AutoUpdater.Common
{
    /// <summary>
    /// Example class demonstrating EventHub usage
    /// </summary>
    public class EventHubExample
    {
        private readonly IEventHub _eventHub;
        private readonly ILogger<EventHubExample> _logger;

        public EventHubExample(IEventHub eventHub, ILogger<EventHubExample> logger)
        {
            _eventHub = eventHub ?? throw new ArgumentNullException(nameof(eventHub));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Demonstrates basic EventHub usage with subscription and publishing
        /// </summary>
        public async Task RunExampleAsync()
        {
            _logger.LogInformation("Starting EventHub example");

            // Subscribe to update events
            using var updateStartedSubscription = _eventHub.Subscribe<UpdateStartedEvent>(OnUpdateStarted);
            using var updateCompletedSubscription = _eventHub.Subscribe<UpdateCompletedEvent>(OnUpdateCompleted);
            using var updateProgressSubscription = _eventHub.Subscribe<UpdateProgressEvent>(OnUpdateProgress);

            // Simulate an update process
            await SimulateUpdateProcessAsync();

            _logger.LogInformation("EventHub example completed");
        }

        private async Task SimulateUpdateProcessAsync()
        {
            const string appName = "example-app";
            const string currentVersion = "1.0.0";
            const string targetVersion = "1.1.0";

            // Publish update started event
            await _eventHub.PublishAsync(new UpdateStartedEvent(appName, currentVersion, targetVersion));

            // Simulate progress updates
            await _eventHub.PublishAsync(new UpdateProgressEvent(appName, "Cloning repository", 20));
            await Task.Delay(100); // Simulate work

            await _eventHub.PublishAsync(new UpdateProgressEvent(appName, "Running migration scripts", 50));
            await Task.Delay(100); // Simulate work

            await _eventHub.PublishAsync(new UpdateProgressEvent(appName, "Starting services", 80));
            await Task.Delay(100); // Simulate work

            await _eventHub.PublishAsync(new UpdateProgressEvent(appName, "Health check", 95));
            await Task.Delay(100); // Simulate work

            // Publish update completed event
            await _eventHub.PublishAsync(new UpdateCompletedEvent(
                appName,
                currentVersion,
                targetVersion,
                true,
                null,
                new[] { "migration_1.0.0_to_1.1.0.sql" }));
        }

        private async Task OnUpdateStarted(UpdateStartedEvent updateStarted)
        {
            _logger.LogInformation("📢 Update started: {App} ({Current} → {Target})",
                updateStarted.ApplicationName,
                updateStarted.CurrentVersion ?? "initial",
                updateStarted.TargetVersion);
            
            await Task.CompletedTask;
        }

        private async Task OnUpdateCompleted(UpdateCompletedEvent updateCompleted)
        {
            var status = updateCompleted.Success ? "✅ SUCCESS" : "❌ FAILED";
            _logger.LogInformation("📢 Update completed: {App} - {Status}",
                updateCompleted.ApplicationName, status);
            
            if (!updateCompleted.Success)
            {
                _logger.LogError("Update failed: {Error}", updateCompleted.ErrorMessage);
            }
            else if (updateCompleted.ExecutedScripts.Count > 0)
            {
                _logger.LogInformation("Executed scripts: {Scripts}", string.Join(", ", updateCompleted.ExecutedScripts));
            }
            
            await Task.CompletedTask;
        }

        private async Task OnUpdateProgress(UpdateProgressEvent updateProgress)
        {
            _logger.LogInformation("📢 Progress: {App} - {Operation} ({Progress}%)",
                updateProgress.ApplicationName,
                updateProgress.Operation,
                updateProgress.ProgressPercentage);
            
            await Task.CompletedTask;
        }
    }
}