# RocketWelder Apps Compose

AutoUpdater deploy package for the **RocketWelder apps image**: the Windows, Linux and Android players, with a manifest,
served to the station's AutoUpdater, which passes downloads on to browsers. The image is pre-built and pulled, never built here.

## Files

| File | Purpose |
|---|---|
| `docker-compose.yml` | The service: pinned image, read-only root, port `${APPS_PORT:-8095}` on the Docker bridge address only, `/health` healthcheck. |
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

The container is not reachable from the LAN. Its port is bound to the Docker bridge's gateway address only
(`APPS_BIND_ADDRESS`, default `172.17.0.1`), which is where the AutoUpdater container reaches the host (its `SshHost`,
set by `install-updater.sh` from docker0). Browsers download through the AutoUpdater at `/apps/files/<name>`, on the
AutoUpdater's own address and port. If a station's docker0 has another address, set `APPS_BIND_ADDRESS` to it in the
package's `.env`; the AutoUpdater already uses that address.
