#!/bin/sh
# SPDX-License-Identifier: MIT
#
# Opens the FRP tunnel to HaRP, then runs the given command.
#
# Written from the documented contract (Nextcloud developer manual, "Adapting ExApps to
# HaRP"): with HP_SHARED_KEY set, frpc logs in to HaRP's FRP server at
# HP_FRP_ADDRESS:HP_FRP_PORT with that key and exposes APP_PORT on HaRP, forwarded to the
# app's unix socket /tmp/exapp.sock. AppAPI copies TLS client certificates to /certs/frp at
# install time; without them the tunnel runs without TLS. Without HP_SHARED_KEY (manual or
# non-HaRP daemons) no tunnel is needed and the app listens on APP_HOST:APP_PORT itself.
set -eu

FRPC_CONFIG=/frpc.toml

if [ -n "${HP_SHARED_KEY:-}" ]; then
    if [ -f /certs/frp/client.crt ] && [ -f /certs/frp/client.key ] && [ -f /certs/frp/ca.crt ]; then
        echo "start.sh: writing $FRPC_CONFIG (TLS)"
        tls='transport.tls.enable = true
transport.tls.certFile = "/certs/frp/client.crt"
transport.tls.keyFile = "/certs/frp/client.key"
transport.tls.trustedCaFile = "/certs/frp/ca.crt"
transport.tls.serverName = "harp.nc"'
    else
        echo "start.sh: writing $FRPC_CONFIG (no TLS certificates in /certs/frp)"
        tls='transport.tls.enable = false'
    fi

    cat > "$FRPC_CONFIG" <<EOF
serverAddr = "${HP_FRP_ADDRESS}"
serverPort = ${HP_FRP_PORT}
loginFailExit = false

${tls}

metadatas.token = "${HP_SHARED_KEY}"

[[proxies]]
remotePort = ${APP_PORT}
type = "tcp"
name = "${APP_ID}"
[proxies.plugin]
type = "unix_domain_socket"
unixPath = "/tmp/exapp.sock"
EOF

    echo "start.sh: starting frpc"
    frpc -c "$FRPC_CONFIG" &
else
    echo "start.sh: HP_SHARED_KEY not set, no FRP tunnel"
fi

echo "start.sh: starting $*"
exec "$@"
