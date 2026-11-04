# An apps image: holds downloadable files, their markdown install guides, app icons and manifest.json, and serves them
# over HTTP. publish-apps.sh appends one COPY per file (one layer per file); see its README for which layers a station
# downloads again after a change. This file alone is the shared base.
# COPY --link/--chmod need BuildKit (the default builder of docker 23+), not a newer frontend.

# alpine 3.20, pinned to its multi-arch index so a rebuild cannot change the base under a release.
# nginx is not pinned: Alpine removes superseded package versions, so a pin would break the next build.
FROM alpine:3.20@sha256:d9e853e87e55526f6b2917df91a2115c36dd7c696a35be12163d44e6e2a4b6bc

RUN apk add --no-cache nginx \
    && mkdir -p /srv/apps/files /srv/apps/guides /srv/apps/icons

COPY nginx.conf /etc/nginx/nginx.conf

USER nginx
EXPOSE 8080
# -e: the compiled-in error log path is not writable on a read-only root; this avoids nginx's start-up alert about it.
ENTRYPOINT ["nginx", "-e", "/dev/stderr", "-g", "daemon off;"]
