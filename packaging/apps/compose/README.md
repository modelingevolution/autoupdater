# Apps compose package (template)

An AutoUpdater deploy package for an apps image built by `../publish-apps.sh`. The image is pre-built and pulled, never
built here. This directory is a template: copy it into the product's own compose repository and set the image
(`registry.example.com/example/apps`) in the three compose files. The scripts read the image from `docker-compose.yml`.

## Files

| File | Purpose |
|---|---|
| `docker-compose.yml` | The service: pinned image, read-only root, port `${APPS_PORT:-8095}` on the Docker bridge address only, `/health` healthcheck. |
| `docker-compose.x64.yml`, `docker-compose.arm64.yml` | Per-arch overrides, layered by AutoUpdater on `uname -m`. |
| `apps.version` | Bare semver of the pinned release. |
| `update-version.sh` | `set X.Y.Z` pins the image tag; `list` queries Harbor for release tags. |
| `release.sh` | Checks first that the tag exists neither locally nor on origin (and stops, changing nothing, if origin cannot be asked). Then pins, generates `up-X.Y.Z.sh`, commits only those files, pushes master, and pushes the git tag AutoUpdater detects. |
| `prune-old-images.sh` | Keeps the two newest apps images on a station. Always exits 0. |

## Register on a station

```bash
./add-package.sh <package-name> <git-url-of-the-product-compose-repo>
```

Then turn the Apps page on in the AutoUpdater (see `../README.md`): `Apps:Enabled=true`,
`Apps:SourceUrl=http://172.17.0.1:8095/`, and `Apps:PackageName=<package-name>` so the page shows the download state.
A different port needs `APPS_PORT` in the package's `.env` and the same port in `Apps:SourceUrl`.

The container is not reachable from the LAN. Its port is bound to the Docker bridge's gateway address only
(`APPS_BIND_ADDRESS`, default `172.17.0.1`), which is where the AutoUpdater container reaches the host. Browsers download
through the AutoUpdater at `/apps/files/<name>`, on the AutoUpdater's own address and port. If a station's docker0 has
another address, set `APPS_BIND_ADDRESS` to it in the package's `.env` and use it in `Apps:SourceUrl`. Otherwise the
bind fails ("cannot assign requested address") and the update with it.

This bind keeps the port tidy, off the LAN for ordinary traffic. It is not a security boundary: Docker turns on IP
forwarding, so a host on the station's network segment that routes 172.17.0.0/16 (or 172.17.0.1) to the station can still
reach the port. What it would reach is the same public set the AutoUpdater's `/apps/files/` already serves to anyone
on the LAN: the manifest, the guides, `/health` and the files. The CI test proves the bind (nothing listens on the LAN
address), not that routed case.
