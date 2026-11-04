# The RocketWelder apps image: holds the player installers and serves them over HTTP.
# publish-apps.sh appends one COPY per file (one layer per file), so a release that changes
# only one app costs a station only that app's layer. This file alone is the shared base.
# COPY --link/--chmod need BuildKit (the default builder of docker 23+), not a newer frontend.

# alpine 3.20, pinned to its multi-arch index so a rebuild cannot change the base under a release.
# nginx is not pinned: Alpine removes superseded package versions, so a pin would break the next build.
FROM alpine:3.20@sha256:d9e853e87e55526f6b2917df91a2115c36dd7c696a35be12163d44e6e2a4b6bc

RUN apk add --no-cache nginx \
    && mkdir -p /srv/apps/files

COPY nginx.conf /etc/nginx/nginx.conf
COPY --chmod=0555 verify-files.sh /usr/local/bin/verify-files.sh

USER nginx
EXPOSE 8080
ENTRYPOINT ["/usr/local/bin/verify-files.sh"]
