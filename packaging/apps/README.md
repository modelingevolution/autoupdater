# Apps image

Downloadable apps (installers, packages) travel to stations as one versioned container image, published from a laptop with
`publish-apps.sh`; there is no CI. A station pulls the image with the credentials it already has, through the AutoUpdater,
like any other package. `docker pull` checks every layer's digest; that is the integrity guarantee.

The image describes itself: `manifest.json` lists the apps, and each app's install guide is a markdown file next to it.
The AutoUpdater's Apps page is a generic renderer of that content and holds no product text of its own.

| Path | What it is |
|---|---|
| `image/` | Dockerfile and nginx config. alpine + nginx, static files only. |
| `publish-apps.sh` | Builds and pushes the image from a laptop. |
| `compose/` | A template AutoUpdater package that deploys the image; copy it into the product's compose repository. |
| `example/` | Example guides and icon, used by the tests and below. |

## Contract

The image serves, as plain static HTTP:

| Path | Answer |
|---|---|
| `GET /manifest.json` | The document below. `Cache-Control: no-store`. |
| `GET /guides/<name>.md` | A guide: `text/markdown; charset=utf-8`, UTF-8, at most 16 KiB. |
| `GET /icons/<name>.png` | An app icon: `image/png`, a PNG of at most 256 KiB. |
| `GET /files/<name>` | A file, with `Content-Disposition: attachment`, `Cache-Control: no-cache` and single-range support. Symlinks are refused. |
| `GET /health` | 200. |
| anything else | 404. No directory listing. |

`manifest.json`, schemaVersion 2:

```json
{
  "schemaVersion": 2,
  "release": "1.2.0",
  "apps": [
    {
      "id": "example-viewer",
      "name": "Example Viewer",
      "icon": "example-viewer.png",
      "downloads": [
        { "platform": "windows", "architecture": "x64",   "version": "2.1.0", "file": "ExampleViewer-2.1.0.msi", "size": 48234496, "guide": "windows.md" },
        { "platform": "linux",   "architecture": "arm64", "version": "2.1.0", "file": "example-viewer_2.1.0_arm64.deb", "size": 292076, "guide": "linux.md", "label": "Kiosk devices" },
        { "platform": "android", "architecture": "arm64", "version": "2.1.0-preview", "file": "ExampleViewer-2.1.0-preview.apk", "size": 2432848, "guide": "android.md", "preview": true }
      ]
    }
  ]
}
```

Per app:

| Field | Rule |
|---|---|
| `schemaVersion` | `2`. Any other value, or none, and the page says the format is not supported and shows no app. |
| `release` | The image tag, `X.Y.Z`. Informational; the AutoUpdater does not read it. |
| `apps` | At most 16; the page shows one card per app, in this order. Only the first 16 are read. |
| `id` | `[a-z0-9][a-z0-9-]{0,31}`. A later app with the same id is left out. |
| `name` | Plain text, one line, at most 80 characters: no control characters, no bidirectional overrides. |
| `icon` | Optional. A file name ending in `.png`, served at `/icons/<icon>`. PNG only (no SVG: it can carry script), at most 256 KiB. Missing, invalid or not a PNG: the card shows a neutral icon. |
| `downloads` | At most 8 per app; the card shows them in this order. |

Per download:

| Field | Rule |
|---|---|
| `platform` | One of `windows`, `linux`, `macos`, `android`, `ios`. Anything else leaves the download out. |
| `architecture` | One of `x64`, `arm64`, `x86`, `arm`, `any`. Anything else leaves the download out. |
| `label` | Optional short variant name, plain text, at most 40 characters (e.g. `Kiosk devices`). A later download with the same platform, architecture and label (compared trimmed and ignoring case) is left out. |
| `preview` | Optional `true`/`false`: shows a "Preview" chip. |
| `version`, `file`, `size` | All three or none. None: the download shows "Not available yet". `version`: `[0-9A-Za-z][0-9A-Za-z.+~_-]{0,63}`; `file`: `[A-Za-z0-9][A-Za-z0-9._+~-]{0,127}`, unique in the image; `size`: bytes, > 0. |
| `guide` | Optional. A file name like `file`, ending in `.md`, served at `/guides/<guide>`. |

An app or download that breaks a rule is left out; the rest of the page still shows. A guide that cannot be read
(missing, over 16 KiB, not UTF-8, or one that fails to parse or render) shows "Install guide unavailable" under its
download.

Guides are rendered as markdown (MudBlazor's markdown component). Raw HTML is not interpreted (it shows as text), images
are not shown (only their alt text), HTML entities such as `&amp;` show as their character, and a link keeps its target
only if it is `http`, `https` or relative. Write the guide for the person installing: requirements, warnings, then
numbered steps. See `example/windows.md`.

Links: a relative link resolves against the AutoUpdater's page, not against the image. Link only to absolute `http` or
`https` URLs, or to a file of this release as `apps/files/<file>`. Any other relative link (`files/x.msi`, `other.md`)
leads nowhere.

## Publish a release

```bash
docker login registry.example.com       # once
./publish-apps.sh --release 1.2.0 --image registry.example.com/example/apps \
    --app example-viewer --name "Example Viewer" --icon example/example-viewer.png \
      --download --platform windows --arch x64 \
                 --file dist/ExampleViewer-2.1.0.msi --version 2.1.0 --guide example/windows.md \
      --download --platform linux --arch arm64 --label "Kiosk devices" \
                 --file dist/example-viewer_2.1.0_arm64.deb --version 2.1.0 --guide example/linux.md \
      --download --platform android --arch arm64 --preview \
                 --file dist/ExampleViewer-2.1.0-preview.apk --version 2.1.0-preview --guide example/android.md
```

Each `--app` starts one app, described by `--name` and `--icon`; each `--download` after it starts one of its
downloads, described by `--platform`, `--arch`, `--label`, `--preview`, `--file` with `--version`, and `--guide`. Two
downloads may share a guide (the same file). The script applies the rules above and refuses what the page would leave
out, including an icon that is not a PNG. It needs bash, docker with buildx, `sha256sum`, `stat`, `iconv` and `od`
(all in Git Bash, WSL and common Linux distributions).

Then pin the release in the compose package (`compose/release.sh 1.2.0` in the product's copy). Stations see the new
version on their Packages page. Nothing downloads until an operator starts it there, or uses "Update all".

To carry an app forward unchanged, pass the same file again: each file, guide and icon is its own layer, its mtime is
derived from its SHA-256, and the image is built with `SOURCE_DATE_EPOCH` and `rewrite-timestamp`, so the parent
directories in a layer carry a fixed time instead of the build time. An unchanged file keeps its layer even when an app
before it changed, and stations do not download it again. A changed file gets another mtime, so its `ETag` and
`Last-Modified` change too: a browser revalidates instead of reusing an older installer, and an `If-Range` resume
started on an older build gets the whole new file rather than a mix of the two.

The script builds with its own buildx builder, `apps-publish` (docker-container driver, created on first use;
`--builder NAME` picks another). Docker's default builder ignores `rewrite-timestamp` with the classic image store, so
the same release would get different layers on different machines. The script refuses a builder that would ignore
`rewrite-timestamp`, i.e. one with the `docker` driver or BuildKit older than 0.13, so `--builder default` cannot
quietly bring the problem back. Pushing both platforms builds the arm64 image under emulation. Docker Desktop has it;
on Linux, install it once with `docker run --privileged --rm tonistiigi/binfmt --install arm64`.

The builder is a container of its own, with its own cache:
- **Disk.** Every installer you publish stays in the builder's cache, outside what `docker system prune` cleans. It
  grows by the size of each new installer. Clear it with `docker buildx prune --builder apps-publish` (`--all` for
  everything). The layers stay identical without the cache: their content and times are fixed.
- **Stop and remove.** `docker buildx stop apps-publish` frees the container between releases, and the next run
  starts it again. `docker buildx rm apps-publish` removes it with its cache.
- **Registry settings.** The builder does not use the Docker daemon's registry settings. `insecure-registries`,
  plain-HTTP registries and certificates added to Docker Desktop or `daemon.json` do not apply to the push. The check
  that the tag does not exist yet runs on the client, so with such a registry it passes, and the push fails after the
  build. For a registry like that, create the builder yourself with a BuildKit config, then publish with
  `--builder NAME`:

  ```toml
  # buildkitd.toml
  [registry."registry.local:5000"]
    http = true                        # plain HTTP
    insecure = true                    # or: a self-signed certificate
    ca = ["/etc/certs/registry-ca.pem"]  # or: trust this CA
  ```

  ```bash
  docker buildx create --name apps-publish-local --driver docker-container --buildkitd-config buildkitd.toml
  ./publish-apps.sh --builder apps-publish-local ...
  ```

  A `ca` path is read inside the builder container: `docker buildx create` copies the files the config names into it.

`publish-apps.sh` refuses a release tag that already exists, and also refuses when it cannot tell (a refused login, no
network): only a clear "not found" from the registry lets it push. `--overwrite` skips that check.

## Turn the page on in the AutoUpdater

The Apps page is off by default: no navigation entry, `/apps` and everything under it (`/apps/files/*`, `/apps/icons/*`)
answer 404, the markdown renderer's script is not sent, and nothing reads the image.

```json
{
  "Apps": {
    "Enabled": true,
    "SourceUrl": "http://172.17.0.1:8095/",
    "Title": "Apps",
    "PackageName": "example-apps"
  }
}
```

| Setting | Meaning |
|---|---|
| `Enabled` | `true` turns the page on. Default `false`. |
| `SourceUrl` | Required when enabled: where the AutoUpdater reads `manifest.json`, the guides and the files (absolute http or https). |
| `Title` | Page title and navigation label, plain text up to 40 characters. Default `Apps`. |
| `PackageName` | Optional: the AutoUpdater package that deploys the image. The page then shows its download progress, failures and available updates. |

Enabled with a missing or invalid setting, the AutoUpdater logs a configuration error at startup and keeps the page off;
it still starts, since it is the station's recovery path. As environment variables: `Apps__Enabled=true`,
`Apps__SourceUrl=...`.

The page reads `manifest.json` when it opens, then each distinct guide and icon once (downloads that share a guide share
one read), at most 4 at a time. Browsers download through the AutoUpdater at `/apps/files/<name>`, which streams the
file from the image (ranges and validators pass through) and only for a name the manifest lists. Icons come from
`/apps/icons/<name>?v=<hash>`, likewise only for a listed icon, and only when it is a PNG; the hash changes with the
icon, so the browser keeps it.

Connections to the image: the AutoUpdater opens at most 16 for file downloads and 8 for manifest, guide and icon reads,
whatever the number of browsers (more requests wait for a free one); the image's nginx allows 256. The icon endpoint
also serves at most 4 requests at once, and answers 503 to one that waits more than 5 s.

## Run the tests

`AppsImageIntegrationTests` builds a real image through `publish-apps.sh` with dummy files. It needs Docker and bash.
On Windows it uses Git Bash (`APPS_TEST_BASH` overrides the path).

```bash
dotnet test tests/ModelingEvolution.AutoUpdater.IntegrationTests/ --filter "FullyQualifiedName~AppsImageIntegrationTests"
```
