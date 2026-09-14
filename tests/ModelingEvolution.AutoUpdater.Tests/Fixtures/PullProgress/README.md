# Recorded docker pull fixtures (epic-106)

Everything here is verbatim tool output recorded on saturn on 2026-09-14; nothing is hand-typed.
Images are public (`public.ecr.aws/docker/library/*`, the ECR mirror of the Docker Hub library images;
Docker Hub itself rate-limited the recording, see `containerd-compose2.40/pull-ratelimited-dockerhub.jsonl`).

`compose.yml`: services `a` and `a2` (same `alpine:3.21.3`), `b` (`busybox:1.36`), `n` (`nginx:1.27-alpine`, whose
bottom layer `f18232174bc9` is the alpine 3.21.3 layer) and the build-only `built`.

| file | command |
|---|---|
| `compose-config.json` | `docker compose -f compose.yml config --format json` |
| `docker-version.txt` | `docker version --format '{{.Server.Os}}/{{.Server.Arch}}'` |
| `manifests/index-<image>.json` | `docker manifest inspect public.ecr.aws/docker/library/<image>` (multi-platform index) |
| `manifests/index-<image>-by-local-digest.json` | same, by the repo digest `docker image ls --digests` shows for the local image |
| `manifests/manifest-<image>-amd64.json` | same, by the index's linux/amd64 entry digest (image manifest with layers) |
| `manifests/manifest-error-notfound.txt` | output of the command for a tag that does not exist (exit 1) |
| `*/image-ls-*.jsonl` | `docker image ls --no-trunc --digests --format '{{json .}}'`, filtered to the fixture repositories |
| `*/pull-*.jsonl` | `docker compose --progress json -f compose.yml pull > file 2>&1` |

Daemons:
- `containerd-compose2.40/`: saturn's docker 29.1.3 (containerd image store), compose 2.40.3.
- `overlay2-compose5.5/`: docker 29.8.0 in a throttled (`tc tbf 3mbit`) `docker:29-dind` with
  `"features":{"containerd-snapshotter":false}` (classic overlay2 store), its bundled compose 5.5.1.
- `overlay2-compose2.40/`: the same dind daemon driven by saturn's compose 2.40.3 (`DOCKER_HOST=tcp://…:2376`).

States: `cold` = none of the images local; `warm` = all local; `partial` = alpine and busybox local, nginx removed
by exact tag (`docker rmi public.ecr.aws/docker/library/nginx:1.27-alpine`).
