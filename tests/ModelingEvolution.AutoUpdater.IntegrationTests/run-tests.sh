#!/bin/bash

# AutoUpdater Integration Test Runner
# This script sets up the environment and runs integration tests

set -e

echo "🚀 AutoUpdater Integration Test Runner"
echo "======================================"

# Check if Docker is available
if ! command -v docker &> /dev/null; then
    echo "❌ Docker is not available. Please install Docker to run integration tests."
    exit 1
fi

# Check if Docker daemon is running
if ! docker info &> /dev/null; then
    echo "❌ Docker daemon is not running. Please start Docker."
    exit 1
fi

echo "✅ Docker is available and running"

# Check if Git submodules are initialized
if [ ! -d "../../examples/testproject/.git" ] || [ ! -d "../../examples/compose/.git" ]; then
    echo "📦 Initializing Git submodules..."
    cd ../..
    git submodule init
    git submodule update
    cd tests/ModelingEvolution.AutoUpdater.IntegrationTests/
    echo "✅ Git submodules initialized"
else
    echo "✅ Git submodules are already initialized"
fi

# Build the test project
echo "🔨 Building integration tests..."
dotnet build

echo "🧪 Running basic infrastructure tests..."
dotnet test --filter "BasicInfrastructureTests" --logger "console;verbosity=normal"

if [ $? -eq 0 ]; then
    echo "✅ Basic infrastructure tests passed"
    
    echo "🧪 Running full integration tests..."
    dotnet test --logger "console;verbosity=normal"
    
    if [ $? -eq 0 ]; then
        echo "🎉 All integration tests passed!"
    else
        echo "❌ Some integration tests failed"
        exit 1
    fi
else
    echo "❌ Basic infrastructure tests failed - Docker environment may not be properly configured"
    exit 1
fi

echo "✅ Integration test run completed successfully"