using FluentAssertions;
using Microsoft.Extensions.Logging;
using ModelingEvolution.AutoUpdater.Common;
using ModelingEvolution.AutoUpdater.Common.Events;
using ModelingEvolution.AutoUpdater.Services;
using NSubstitute;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace ModelingEvolution.AutoUpdater.Tests.Services
{
    public class ProgressServiceTests
    {
        private readonly IEventHub _eventHub = Substitute.For<IEventHub>();
        private readonly ProgressService _service;

        public ProgressServiceTests()
        {
            _service = new ProgressService(_eventHub, Substitute.For<ILogger<ProgressService>>());
        }

        [Fact]
        public async Task ProgressEvents_ArePublishedInOrderWithPinnedState_AndDeduplicated()
        {
            var published = new List<(string Op, int Pct)>();
            _eventHub.PublishAsync(Arg.Do<UpdateProgressEvent>(e => published.Add((e.Operation, e.ProgressPercentage))))
                     .Returns(Task.CompletedTask);
            _service.StartOperation("Update", "app");

            _service.LogPhaseProgress("Pulling Docker images (0/2)", 30f, 10f, "a");
            _service.LogPhaseProgress("Pulling Docker images (0/2)", 30f, 20f, "b");   // same visible triple: no event
            _service.LogPhaseProgress("Pulling Docker images (1/2)", 35f, 5f, "c");
            _service.LogPhaseProgress("Pulling Docker images (2/2)", 40f, 100f, "d");
            _service.LogOperationProgress("Creating backup", 40);
            await _service.PublishedEvents;

            published.Should().Equal(
                ("Update", 0),
                ("Pulling Docker images (0/2)", 30),
                ("Pulling Docker images (1/2)", 35),
                ("Pulling Docker images (2/2)", 40),
                ("Creating backup", 40));
        }

        [Fact]
        public void LogPhaseProgress_SetsOperationOverallAndPhaseInOneNotification()
        {
            var notifications = 0;
            _service.Changed += () => notifications++;
            _service.StartOperation("Update", "app");
            notifications = 0;

            _service.LogPhaseProgress("Pulling Docker images (1/2)", 35f, 42.5f, "Downloading 412.0 MB / 1.2 GB");

            notifications.Should().Be(1);
            _service.CurrentOperation.Should().Be("Pulling Docker images (1/2)");
            _service.ProgressPercentage.Should().Be(35);
            _service.PhaseProgress.Should().Be(42.5f);
            _service.PhaseMessage.Should().Be("Downloading 412.0 MB / 1.2 GB");
        }

        [Fact]
        public void LogPhaseProgress_WithNullPhasePercentage_KeepsPhaseVisibleAsIndeterminate()
        {
            _service.LogPhaseProgress("Pulling Docker images", 30f, null, "Resolving images");

            _service.PhaseMessage.Should().Be("Resolving images");
            _service.PhaseProgress.Should().BeNull();
        }

        [Fact]
        public void LogPhaseProgress_ClampsPhasePercentage()
        {
            _service.LogPhaseProgress("op", 30f, 250f, "msg");

            _service.PhaseProgress.Should().Be(100f);
        }

        [Fact]
        public void LogOperationProgress_ClearsPhase()
        {
            _service.LogPhaseProgress("Pulling Docker images (2/2)", 40f, 100f, "Downloading 1.2 GB / 1.2 GB");

            _service.LogOperationProgress("Creating backup", 40);

            _service.PhaseMessage.Should().BeNull();
            _service.PhaseProgress.Should().BeNull();
            _service.CurrentOperation.Should().Be("Creating backup");
        }

        [Fact]
        public void UpdateOperation_ClearsPhase()
        {
            _service.LogPhaseProgress("op", 30f, 10f, "msg");

            _service.UpdateOperation("next");

            _service.PhaseMessage.Should().BeNull();
        }

        [Fact]
        public void StartOperation_ClearsPhase()
        {
            _service.LogPhaseProgress("op", 30f, 10f, "msg");

            _service.StartOperation("Update", "app");

            _service.PhaseMessage.Should().BeNull();
        }

        [Fact]
        public void CompleteOperation_ClearsPhase()
        {
            _service.LogPhaseProgress("op", 30f, 10f, "msg");

            _service.CompleteOperation();

            _service.PhaseMessage.Should().BeNull();
        }

        [Fact]
        public void Reset_ClearsPhase()
        {
            _service.LogPhaseProgress("op", 30f, 10f, "msg");

            _service.Reset();

            _service.PhaseMessage.Should().BeNull();
        }
    }
}
