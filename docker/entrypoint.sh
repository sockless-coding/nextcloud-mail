#!/bin/sh
# SPDX-License-Identifier: MIT
#
# Runs as root (the image's default user, so AppAPI can docker cp certificates and run
# update-ca-certificates), fixes ownership of the paths the app needs, then drops to the
# unprivileged service user for frpc and the app.
set -eu

APP_USER=sockless

if [ "$(id -u)" = "0" ]; then
    # AppAPI copies FRP certificates in as root at install time.
    if [ -d /certs/frp ]; then
        chown -R "$APP_USER:$APP_USER" /certs/frp
    fi
    # The data volume is created root-owned by Docker.
    if [ -n "${APP_PERSISTENT_STORAGE:-}" ]; then
        mkdir -p "$APP_PERSISTENT_STORAGE"
        chown -R "$APP_USER:$APP_USER" "$APP_PERSISTENT_STORAGE"
        chmod 700 "$APP_PERSISTENT_STORAGE"
    fi
    exec su-exec "$APP_USER" /start.sh "$@"
fi

exec /start.sh "$@"
