# The RocketWelder apps image: holds the player installers and serves them over HTTP.
# publish-apps.sh appends one COPY per file (one layer per file), so a release that changes
# only one app costs a station only that app's layer. This file alone is the shared base.
FROM alpine:3.20

RUN apk add --no-cache nginx \
    && mkdir -p /srv/apps/files

COPY nginx.conf /etc/nginx/nginx.conf
COPY --chmod=0555 verify-files.sh /usr/local/bin/verify-files.sh

USER nginx
EXPOSE 8080
ENTRYPOINT ["/usr/local/bin/verify-files.sh"]
