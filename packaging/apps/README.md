# RocketWelder apps image

The players travel as a versioned container image in our registry. A station pulls it with the credentials it already
has, through the AutoUpdater, like any other package. The container verifies and serves the files. The AutoUpdater's
Apps page only reads the container's manifest and shows download links; it has no download logic of its own.

| Path | What it is |
|---|---|
| `image/` | Dockerfile, nginx config and the start-up check. alpine + nginx; about 5 MB on top of the files. |
| `publish-apps.sh` | Builds and pushes the image from a laptop. |
| `compose/` | The AutoUpdater package. Becomes the `rocketwelder-apps-compose` repo. |

## Publish a release

```bash
docker login docker.modelingevolution.com       # once
./publish-apps.sh --release 1.2.0 \
    --windows dist/RocketWelderClient-Setup.exe --windows-version 2.1.0 \
    --android dist/native-player-kiosk-2.1.0-arm64-v8a.apk --android-version 2.1.0 \
    --linux   dist/native-player_2.1.0_arm64.deb --linux-version 2.1.0
```

Then, in `rocketwelder-apps-compose`: `./release.sh 1.2.0`. Stations see the new version on their Packages page. Nothing
downloads until an operator starts it there, or uses "Update all".

An app left out of the command shows as "Not available yet". To carry an app forward unchanged, pass the same file again:
each file is its own layer with a fixed mtime, so a station does not download it twice.

## What the container serves

| Path | Answer |
|---|---|
| `/files/<name>` | The file, with `Content-Disposition: attachment` and range support. 503 if it failed verification. |
| `/manifest.json` | `release` and, per app: `platform`, `version`, `file`, `size`, `sha256`. |
| `/verification.json` | `state` (`verified` or `failed`) and `blocked`: the result of hashing every file at start. |
| `/health` | 200 once verification has finished. |
| anything else | 404. No directory listing. |

## Run the tests

`AppsImageIntegrationTests` builds a real image through `publish-apps.sh` with dummy files. It needs Docker and bash.
On Windows it uses Git Bash (`APPS_TEST_BASH` overrides the path).

```bash
dotnet test tests/ModelingEvolution.AutoUpdater.IntegrationTests/ --filter "FullyQualifiedName~AppsImageIntegrationTests"
```
