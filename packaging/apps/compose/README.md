# RocketWelder Apps Compose

AutoUpdater deploy package for the **RocketWelder apps image**: the Windows, Linux and Android players, with a manifest,
served over HTTP on the station's LAN. The image is pre-built and pulled, never built here.

## Files

| File | Purpose |
|---|---|
| `docker-compose.yml` | The service: pinned image, read-only root, port `${APPS_PORT:-8095}`, `/health` healthcheck. |
| `docker-compose.x64.yml`, `docker-compose.arm64.yml` | Per-arch overrides, layered by AutoUpdater on `uname -m`. |
| `rocketwelder-apps.version` | Bare semver of the pinned release. |
| `update-version.sh` | `set X.Y.Z` pins the image tag; `list` queries Harbor for release tags. |
| `release.sh` | Checks first that the tag exists neither locally nor on origin (and stops, changing nothing, if origin cannot be asked). Then pins, generates `up-X.Y.Z.sh`, commits only those files, pushes master, and pushes the git tag AutoUpdater detects. |
| `prune-old-images.sh` | Keeps the two newest apps images on a station. Always exits 0. |

## Register on a station

```bash
./add-package.sh rocketwelder-apps https://github.com/modelingevolution/rocketwelder-apps-compose.git
```

The station then pulls with the registry login its host already has, because the image is in the same Harbor project as
the rocket-welder image. The package name must stay `rocketwelder-apps`, or set `Apps:PackageName` in the AutoUpdater.
A different port needs `APPS_PORT` in the package's `.env` and `Apps:Port` in the AutoUpdater.
