#!/bin/bash

# AutoUpdater Startup Script
echo "🚀 Starting ModelingEvolution AutoUpdater..."

# Check if Docker is running
if ! docker info > /dev/null 2>&1; then
    echo "❌ Docker is not running. Please start Docker first."
    exit 1
fi

# Create data directory if it doesn't exist
mkdir -p data

# Copy example configuration if no config exists
if [ ! -f "data/appsettings.json" ]; then
    echo "📄 Creating default configuration..."
    cp data/appsettings.example.json data/appsettings.json
    echo "✅ Configuration created at data/appsettings.json"
fi

# Set default SSH_USER if not provided
if [ -z "$SSH_USER" ]; then
    export SSH_USER=deploy
    echo "🔧 Using default SSH_USER: $SSH_USER"
fi

# Create .env file if it doesn't exist
if [ ! -f ".env" ]; then
    echo "📄 Creating .env file..."
    cat > .env << EOF
SSH_USER=$SSH_USER
SSH_PASSWORD=
HOST_ADDRESS=172.17.0.1
EOF
    echo "✅ .env file created (you can edit it to customize settings)"
fi

echo "🏗️  Building and starting AutoUpdater..."

# Build and start the services
docker-compose up --build

echo "🎉 AutoUpdater is starting!"
echo "📱 Web UI will be available at: http://localhost:8080"
echo "🔌 API endpoints available at: http://localhost:8080/api/"
echo ""
echo "API Endpoints:"
echo "  GET  /api/packages        - List all packages"
echo "  GET  /api/upgrades/{name} - Check upgrade status"
echo "  POST /api/update/{name}   - Trigger package update"
echo "  POST /api/update-all      - Trigger all updates"