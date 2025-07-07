#!/bin/bash

# AutoUpdater Integration Test Environment Setup Script
# This script prepares the test environment for integration testing

set -e

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
cd "$SCRIPT_DIR"

# Colors for output
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m'

echo -e "${GREEN}Setting up AutoUpdater Integration Test Environment${NC}"
echo "=================================================="

# 1. Generate SSH keys if they don't exist
if [ ! -f "./ssh-keys/id_rsa" ]; then
    echo -e "${YELLOW}Generating SSH key pair...${NC}"
    mkdir -p ./ssh-keys
    ssh-keygen -t rsa -b 4096 -f ./ssh-keys/id_rsa -N "" -C "autoupdater-test@localhost"
    chmod 600 ./ssh-keys/id_rsa
    chmod 644 ./ssh-keys/id_rsa.pub
    echo -e "${GREEN}SSH keys generated${NC}"
else
    echo -e "${GREEN}SSH keys already exist${NC}"
fi

# 2. Create test repository directory
echo -e "${YELLOW}Creating test repository directory...${NC}"
mkdir -p ./test-repos

# 3. Update SSH host configuration in AutoUpdater config
echo -e "${YELLOW}Updating SSH host configuration...${NC}"
# The test environment uses 'test-ssh-host' as the SSH host
sed -i.bak 's/"172.17.0.1"/"test-ssh-host"/g' ../../src/ModelingEvolution.AutoUpdater/UpdateHost.cs 2>/dev/null || true

# 4. Build TestProject images
echo -e "${YELLOW}Building TestProject images...${NC}"
cd ../testproject

# Build version 1.0.0
echo "Building testproject:1.0.0..."
docker build -t testproject:1.0.0 --build-arg VERSION=1.0.0 .

# Build version 1.1.0
echo "Building testproject:1.1.0..."
docker build -t testproject:1.1.0 --build-arg VERSION=1.1.0 .

cd "$SCRIPT_DIR"

# 5. Create a test Git repository
echo -e "${YELLOW}Setting up test Git repository...${NC}"
TEST_REPO_DIR="./test-repos/testproject"
rm -rf "$TEST_REPO_DIR"
mkdir -p "$TEST_REPO_DIR"

# Copy TestProject files to test repo
cp -r ../testproject/* "$TEST_REPO_DIR/"
cd "$TEST_REPO_DIR"

# Initialize Git repository
git init
git config user.email "test@autoupdater.local"
git config user.name "AutoUpdater Test"
git add .
git commit -m "Initial commit"
git tag v1.0.0

# Create a branch for version 1.1.0
git checkout -b update-1.1.0
# Update version in project file
sed -i 's/<InformationalVersion>1.0.0<\/InformationalVersion>/<InformationalVersion>1.1.0<\/InformationalVersion>/g' src/TestProject.csproj
git add .
git commit -m "Update to version 1.1.0"
git tag v1.1.0

# Go back to v1.0.0
git checkout v1.0.0

cd "$SCRIPT_DIR"

# 6. Display next steps
echo ""
echo -e "${GREEN}Test environment setup complete!${NC}"
echo ""
echo "Next steps:"
echo "1. Start the test environment:"
echo "   docker-compose -f docker-compose.test.yml up -d"
echo ""
echo "2. Wait for services to be healthy:"
echo "   docker-compose -f docker-compose.test.yml ps"
echo ""
echo "3. Run integration tests:"
echo "   dotnet test ../../tests/ModelingEvolution.AutoUpdater.IntegrationTests"
echo ""
echo "4. View logs:"
echo "   docker-compose -f docker-compose.test.yml logs -f autoupdater"
echo ""
echo "5. Cleanup:"
echo "   docker-compose -f docker-compose.test.yml down -v"