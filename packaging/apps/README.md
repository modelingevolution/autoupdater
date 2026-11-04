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
| `example/` | Example guides, used by the tests and below. |

## Contract

The image serves, as plain static HTTP:

| Path | Answer |
|---|---|
| `GET /manifest.json` | The document below. `Cache-Control: no-store`. |
| `GET /guides/<name>.md` | A guide: `text/markdown; charset=utf-8`, UTF-8, at most 64 KiB. |
| `GET /files/<name>` | A file, with `Content-Disposition: attachment`, `Cache-Control: no-cache` and single-range support. Symlinks are refused. |
| `GET /health` | 200. |
| anything else | 404. No directory listing. |

`manifest.json`, schemaVersion 2:

```json
{
  "schemaVersion": 2,
  "release": "1.2.0",
  "apps": [
    {"id": "windows", "title": "Example Viewer for Windows",
     "version": "2.1.0", "file": "ExampleViewer-2.1.0.msi", "size": 48234496, "guide": "windows.md"},
    {"id": "android", "title": "Example Viewer for Android", "guide": "android.md"}
  ]
}
```

| Field | Rule |
|---|---|
| `schemaVersion` | `2`. Any other value, or none, and the page says the format is not supported and shows no app. |
| `release` | The image tag, `X.Y.Z`. |
| `apps` | At most 16; the page shows them in this order. Only the first 16 are read. |
| `id` | `[a-z0-9][a-z0-9-]{0,31}`, unique. |
| `title` | Plain text, one line, at most 80 characters: no control characters, no bidirectional overrides. |
| `version`, `file`, `size` | All three or none. None: the app shows "Not available yet". `version`: `[0-9A-Za-z][0-9A-Za-z.+~_-]{0,63}`; `file`: `[A-Za-z0-9][A-Za-z0-9._+~-]{0,127}`, unique; `size`: bytes, > 0. |
| `guide` | Optional. A file name like `file`, ending in `.md`, served at `/guides/<guide>`. |

An entry that breaks a rule is left out; the rest of the page still shows. A guide that cannot be read (missing, over
64 KiB, not UTF-8) shows "Install guide unavailable" on its app's card.

Guides are rendered as markdown (MudBlazor's markdown component). Raw HTML is not interpreted (it shows as text), images
are replaced by their alt text, and a link keeps its target only if it is `http`, `https` or relative. Write the guide
for the person installing: requirements, warnings, then numbered steps. See `example/windows.md`.

## Publish a release

```bash
docker login registry.example.com       # once
./publish-apps.sh --release 1.2.0 --image registry.example.com/example/apps \
    --app windows --title "Example Viewer for Windows" \
          --file dist/ExampleViewer-2.1.0.msi --version 2.1.0 --guide example/windows.md \
    --app android --title "Example Viewer for Android" --guide example/android.md
```

Each `--app` starts one app; `--title`, `--file` with `--version`, and `--guide` after it describe that app. The script
applies the rules above and refuses what the page would leave out. It needs bash, docker with buildx, `sha256sum`,
`stat` and `iconv` (all in Git Bash, WSL and common Linux distributions).

Then pin the release in the compose package (`compose/release.sh 1.2.0` in the product's copy). Stations see the new
version on their Packages page. Nothing downloads until an operator starts it there, or uses "Update all".

To carry an app forward unchanged, pass the same file again: each file is its own layer, and its mtime is derived from
its SHA-256. The build clamps every other timestamp (`SOURCE_DATE_EPOCH` with `rewrite-timestamp`), so an unchanged
file or guide keeps its layer even when an app before it changed, and a station does not download it twice. This needs
buildx with BuildKit 0.13 or later. A changed file gets another
mtime, so its `ETag` and `Last-Modified` change too: a browser revalidates instead of reusing an older installer, and an
`If-Range` resume started on an older build gets the whole new file rather than a mix of the two.

`publish-apps.sh` refuses a release tag that already exists, and also refuses when it cannot tell (a refused login, no
network): only a clear "not found" from the registry lets it push. `--overwrite` skips that check.

## Turn the page on in the AutoUpdater

The Apps page is off by default: no navigation entry, `/apps` and `/apps/files/*` answer 404, and nothing reads the image.

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

The page reads `manifest.json` and the guides once, when it opens. Browsers download through the AutoUpdater at
`/apps/files/<name>`, which streams the file from the image (ranges and validators pass through) and only for a name the
manifest lists.

## Run the tests

`AppsImageIntegrationTests` builds a real image through `publish-apps.sh` with dummy files. It needs Docker and bash.
On Windows it uses Git Bash (`APPS_TEST_BASH` overrides the path).

```bash
dotnet test tests/ModelingEvolution.AutoUpdater.IntegrationTests/ --filter "FullyQualifiedName~AppsImageIntegrationTests"
```
