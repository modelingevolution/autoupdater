# AutoUpdater Publish & Update Process

## Overview
This document describes the end-to-end process for publishing a new version of the AutoUpdater application and deploying it to a remote machine using the REST API.

## Release Pipeline Architecture

```
Code Changes → Commit → Push → Release Script → CI/CD Build →
Docker Registry → Tag Push → Compose Update → API-Triggered Update → Remote Deployment
```

## Step-by-Step Process

### 1. Code Changes & Local Testing
**What We Did (v1.0.72)**:
- Fixed bug in `UpdateService.cs`:
  - Changed `PackageInfo.Name` default from `string.Empty` to `PackageName.Empty`
  - Replaced local `File.Exists()` with `IDeploymentStateProvider` for remote file reading
  - Added `IDeploymentStateProvider` dependency injection

**Best Practice**: Always test changes locally before committing
```bash
dotnet build
dotnet test
```

### 2. Git Commit & Push
**Commands Used**:
```bash
git add -A
git commit -m "Fix API package retrieval and current version detection

- Fix PackageInfo.Name default value from string.Empty to PackageName.Empty
- Replace local File.Exists() with IDeploymentStateProvider
- Add IDeploymentStateProvider dependency injection

🤖 Generated with [Claude Code](https://claude.com/claude-code)

Co-Authored-By: Claude <noreply@anthropic.com>"

git push
```

**Best Practice**:
- Write clear commit messages explaining WHAT and WHY
- Include co-authorship attribution when appropriate
- Commit frequently with atomic, focused changes

### 3. Release Script Execution
**Command**:
```bash
./release.sh --patch -y -m "Fix API package retrieval and version detection"
```

**What It Does**:
1. Auto-increments version: `1.0.71` → `1.0.72`
2. Fetches latest changes from remote
3. Checks if Docker images exist for current commit (preview images)
4. Creates and pushes git tag: `v1.0.72`
5. Triggers CI/CD promotion workflow

**Script Behavior**:
- Validates Docker images exist before tagging (fail-fast)
- Uses `-y` flag for non-interactive execution
- Includes release message for documentation

**Best Practice**:
- Always include release notes (`-m` flag)
- Use semantic versioning (major.minor.patch)
- Verify preview images before promoting

### 4. CI/CD Promotion
**Automatic Process (GitHub Actions)**:
When tag `v1.0.72` is pushed:
1. Promotion workflow detects new tag
2. Pulls preview image: `modelingevolution/autoupdater:preview-95ef1ce`
3. Re-tags and pushes production images:
   - `modelingevolution/autoupdater:latest`
   - `modelingevolution/autoupdater:1.0.72`
   - `modelingevolution/autoupdater:1.0`
   - `modelingevolution/autoupdater:1`

**Wait Time**: ~30 seconds for images to propagate

**Best Practice**:
- Monitor GitHub Actions workflow for errors
- Wait for successful promotion before updating compose configs

### 5. Update Compose Repository
**Location**: `/mnt/d/source/modelingevolution/autoupdater-compose`

**Commands**:
```bash
cd /mnt/d/source/modelingevolution/autoupdater-compose
sleep 30  # Wait for Docker image promotion
./update-version.sh update
```

**What `update-version.sh` Does**:
1. Fetches latest version from Docker Hub: `1.0.72`
2. Updates `autoupdater.version` file
3. Updates `docker-compose.yml` default image version
4. Regenerates `install.sh` from template
5. Updates checksums for integrity verification

**Files Modified**:
- `autoupdater.version`: `1.0.71` → `1.0.72`
- `docker-compose.yml`: Image tag updated
- `install.sh`: Regenerated with new checksums
- `checksums.txt`: Updated hashes

**Commit & Push**:
```bash
git add -A
git commit -m "Update AutoUpdater to version 1.0.72"
git push
```

**Best Practice**:
- Always wait for image promotion before updating compose
- Verify version detection worked correctly
- Commit compose changes immediately

### 6. Tag Compose Repository
**Commands**:
```bash
git tag v1.0.73  # Next version tag
git push --tags
```

**Important Notes**:
- Compose repo version (v1.0.73) ≠ Image version (1.0.72)
- Compose version tracks configuration changes
- Image version tracks application code

**Best Practice**:
- Tag compose repo after every update
- Use sequential versioning for compose tags
- Document version relationships

### 7. Remote Update via REST API
**Trigger Update**:
```bash
ssh user@remote "curl -X POST http://localhost:8080/api/update/autoupdater -s | jq ."
```

**API Response**:
```json
{
  "packageName": "autoupdater",
  "updateId": "6bbb5bc6-ca3a-4e3d-9967-98b893f975a7",
  "status": "started",
  "message": "Update process initiated"
}
```

**What Happens on Remote**:
1. AutoUpdater receives POST request
2. Checks for available updates
3. Pulls latest image from Docker registry
4. Stops current container
5. Updates compose configuration
6. Starts new container
7. Updates `deployment.state.json` with new version
8. Runs any migration scripts (if present)

**Monitor Progress**:
```bash
# Watch logs
ssh user@remote "docker logs autoupdater --follow"

# Check status
ssh user@remote "docker compose -p autoupdater ps"
```

**Best Practice**:
- Always monitor logs during update
- Wait ~60 seconds for container startup
- Verify health check passes

### 8. Verification
**Check Package API**:
```bash
ssh user@remote "curl -s http://localhost:8080/api/packages | jq ."
```

**Expected Response**:
```json
{
  "packages": [
    {
      "name": "autoupdater",
      "currentVersion": "v1.0.73",
      "status": "running"
    }
  ]
}
```

**Verification Checklist**:
- [x] Container is running and healthy
- [x] API returns current version correctly
- [x] No errors in logs
- [x] Application functionality works
- [x] Health check endpoint responds

**Best Practice**:
- Test critical API endpoints after update
- Verify database migrations succeeded (if any)
- Check application logs for errors
- Test key user workflows

## Common Issues & Solutions

### Issue 1: Docker Image Not Found
**Symptom**: Update fails with "image not found"
**Cause**: CI/CD promotion not yet complete
**Solution**: Wait 30-60 seconds and retry

### Issue 2: Version Mismatch
**Symptom**: Compose shows different version than image
**Cause**: Compose repo and image repo have different versioning
**Solution**: This is expected - compose version tracks config, image version tracks code

### Issue 3: Health Check Fails
**Symptom**: Container starts but health check fails
**Cause**: Application error on startup
**Solution**:
1. Check logs: `docker logs autoupdater`
2. Rollback if needed: Update to previous version
3. Fix issue and release new version

### Issue 4: API Returns 500 Error
**Symptom**: API endpoints return internal server error
**Cause**: Code bug or configuration issue
**Solution**:
1. Check application logs
2. Verify configuration files
3. Test locally to reproduce
4. Fix and publish patch release

## Best Practices Summary

### Before Release
1. **Test Locally**: Build and test all changes
2. **Review Changes**: Ensure no sensitive data in commits
3. **Update Documentation**: Keep specs and docs current

### During Release
1. **Atomic Commits**: One logical change per commit
2. **Clear Messages**: Explain what and why
3. **Semantic Versioning**: Use appropriate version increments
4. **Wait for CI/CD**: Verify promotion succeeds

### After Release
1. **Monitor Deployment**: Watch logs during update
2. **Verify Functionality**: Test critical paths
3. **Document Issues**: Track any problems encountered
4. **Update Changelogs**: Keep release notes current

## Rollback Procedure

If an update fails:

### Option 1: API Rollback (Future Feature)
```bash
# Restore previous backup with version
curl -X POST http://localhost:8080/api/backup/autoupdater/restore \
  -d '{"filename": "backup-previous-version.tar.gz"}'
```

### Option 2: Manual Rollback
```bash
# Update to previous version
cd /var/docker/configuration/autoupdater
git checkout v1.0.71  # Previous version tag
docker compose down
docker compose up -d
```

### Option 3: Compose Update
```bash
# Update via API to specific version
curl -X POST http://localhost:8080/api/update/autoupdater \
  -d '{"targetVersion": "v1.0.71"}'
```

## Monitoring & Observability

### Key Metrics to Watch
- Container health status
- API response times
- Error rates in logs
- Memory/CPU usage
- Disk space (especially for backups)

### Logging Strategy
- All update operations logged
- Success/failure tracked
- Errors include stack traces
- Deployment state persisted

### Health Checks
- HTTP endpoint: `/health`
- Container health check: Every 30s
- Startup grace period: 40s
- Failure threshold: 3 retries

## Security Considerations

### Git Commits
- Never commit secrets or credentials
- Use environment variables for sensitive data
- Review diffs before pushing

### Docker Images
- Only pull from trusted registries
- Verify image checksums when possible
- Use specific tags, not `latest` in production

### API Security
- Currently no authentication (future enhancement)
- Rate limiting recommended
- Audit all update operations
- Restrict API access to internal network

## Tools & Scripts Reference

### Release Scripts
- `release.sh`: Automated release process
- `update-version.sh`: Update compose configuration
- `update-checksums.sh`: Regenerate integrity hashes

### API Endpoints
- `POST /api/update/{packageName}`: Trigger update
- `GET /api/packages`: List packages and versions
- `GET /api/upgrades/{packageName}`: Check for updates
- `POST /api/update-all`: Update all packages

### Docker Commands
```bash
# Check running containers
docker compose -p autoupdater ps

# View logs
docker logs autoupdater --tail 100 --follow

# Restart container
docker compose -p autoupdater restart

# Pull latest image
docker compose -p autoupdater pull
```

## Conclusion

This process ensures:
- ✅ Automated, repeatable releases
- ✅ Version tracking across repositories
- ✅ Safe deployment with verification
- ✅ Easy rollback capabilities
- ✅ Full audit trail

The entire process from code change to production deployment takes approximately 2-3 minutes when automated, with most time spent waiting for CI/CD pipeline.
