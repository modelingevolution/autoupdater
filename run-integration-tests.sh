#!/bin/bash

# Run AutoUpdater Integration Tests

echo "🧪 Running AutoUpdater Integration Tests..."

# Set working directory to script location
cd "$(dirname "$0")"

# Check if Docker is running
if ! docker info > /dev/null 2>&1; then
    echo "❌ Docker is not running. Please start Docker first."
    exit 1
fi

# Clean up any existing test containers
echo "🧹 Cleaning up existing test containers..."
docker compose -p autoupdater-test down -v 2>/dev/null || true

# Ensure SSH keys exist for tests
if [ ! -f "./data/ssh/id_rsa" ]; then
    echo "⚠️  SSH keys not found. Please run ./install-ssh-keys.sh first."
    exit 1
fi

# Run the integration tests
echo "🚀 Starting integration tests..."
dotnet test tests/ModelingEvolution.AutoUpdater.IntegrationTests \
    --logger "console;verbosity=detailed" \
    --configuration Debug \
    -- xunit.parallelExecution.disable=true

TEST_RESULT=$?

# Clean up test containers
echo "🧹 Cleaning up test containers..."
docker compose -p autoupdater-test down -v 2>/dev/null || true

if [ $TEST_RESULT -eq 0 ]; then
    echo "✅ Integration tests passed!"
else
    echo "❌ Integration tests failed with exit code $TEST_RESULT"
fi

exit $TEST_RESULT