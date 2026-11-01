#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
#
# Development loops for Sockless Mail against the stack in dev/docker-compose.yml.
# Works in Git Bash on Windows, Linux and macOS. Run `dev/dev.sh help` for the commands.
set -euo pipefail

# Git Bash would otherwise rewrite container paths such as /tmp/info.xml into Windows paths.
export MSYS_NO_PATHCONV=1

# With path conversion off, Windows tools need native paths: pwd -W (Git Bash) gives F:/...
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && { pwd -W 2>/dev/null || pwd; })"
COMPOSE=(docker compose -f "$ROOT/dev/docker-compose.yml")
APP_ID=sockless_mail
INFO_XML="$ROOT/appinfo/info.xml"
VERSION="$(sed -n 's:.*<version>\(.*\)</version>.*:\1:p' "$INFO_XML" | head -1)"
IMAGE_TAG="$(sed -n 's:.*<image-tag>\(.*\)</image-tag>.*:\1:p' "$INFO_XML" | head -1)"
REGISTRY="$(sed -n 's:.*<registry>\(.*\)</registry>.*:\1:p' "$INFO_XML" | head -1)"
IMAGE="$REGISTRY/$(sed -n 's:.*<image>\(.*\)</image>.*:\1:p' "$INFO_XML" | head -1):$IMAGE_TAG"

DAEMON_HARP=harp_dev
DAEMON_MANUAL=manual_dev
HP_SHARED_KEY="${HP_SHARED_KEY:-dev-only-harp-key-0123456789abcdef}"
# Dev-only values. The manual loop's secret and port must match launchSettings.json.
APP_SECRET=dev-only-secret-sockless-mail-0123456789
APP_PORT=9080
ENCRYPTION_KEY=dev-only-encryption-key-sockless-mail-0123456789

occ() { "${COMPOSE[@]}" exec -T -u www-data nextcloud php occ "$@"; }

# Manual-install registration: the JSON form can pin secret and port. Keep the routes in
# sync with appinfo/info.xml (access_level 0 = PUBLIC, 1 = USER, 2 = ADMIN).
json_info() {
    cat <<EOF
{"id":"$APP_ID","name":"Sockless Mail","version":"$VERSION","secret":"$APP_SECRET","port":$APP_PORT,
 "routes":[{"url":"^/(js|css|img)/.*\$","verb":"GET","access_level":0},
           {"url":"^/oauth/callback\$","verb":"GET","access_level":0},
           {"url":"^/files-action/send\$","verb":"POST","access_level":1},
           {"url":"^/api/admin(/.*)?\$","verb":"GET,PUT","access_level":2},
           {"url":"^/api(/.*)?\$","verb":"GET,POST,PUT,DELETE","access_level":1}]}
EOF
}

cmd_up() {
    "${COMPOSE[@]}" up -d
    echo "Waiting for Nextcloud to finish installing (first start takes a minute)..."
    for _ in $(seq 1 120); do
        if occ status --output=json 2>/dev/null | grep -q '"installed":true'; then break; fi
        sleep 2
    done
    occ status --output=json | grep -q '"installed":true' || { echo "Nextcloud did not come up; see: ${COMPOSE[*]} logs nextcloud"; exit 1; }

    occ app:enable app_api >/dev/null
    occ app:enable socklessmail >/dev/null
    # Its welcome modal covers every page of a fresh instance.
    occ app:disable firstrunwizard >/dev/null || true
    local daemons; daemons="$(occ app_api:daemon:list)"
    if ! grep -q "$DAEMON_HARP" <<<"$daemons"; then
        occ app_api:daemon:register "$DAEMON_HARP" "HaRP (dev)" docker-install http harp:8780 http://proxy \
            --net sockless-dev --harp --harp_frp_address harp:8782 --harp_shared_key "$HP_SHARED_KEY" --set-default
    fi
    # Deploy locally built images: skip the pull for info.xml's registry (also on older stacks).
    if ! occ app_api:daemon:registry:list "$DAEMON_HARP" | grep -qxF " - $REGISTRY -> local"; then
        occ app_api:daemon:registry:add "$DAEMON_HARP" --registry-from "$REGISTRY" --registry-to local
    fi
    if ! grep -q "$DAEMON_MANUAL" <<<"$daemons"; then
        occ app_api:daemon:register "$DAEMON_MANUAL" "Manual (dev)" manual-install http host.docker.internal http://proxy
    fi
    occ app_api:daemon:list
    echo "Nextcloud: http://localhost:8180 (admin/admin)"
}

cmd_frontend() {
    (cd "$ROOT/src/frontend" && { [ -d node_modules ] || npm ci --no-audit --no-fund; } && npm run build)
}

cmd_run() {
    cmd_frontend
    exec dotnet run --project "$ROOT/src/SocklessMail.Api" --launch-profile manual-install
}

cmd_register_manual() {
    occ app_api:app:unregister "$APP_ID" --force --silent || true
    occ app_api:app:register "$APP_ID" "$DAEMON_MANUAL" --json-info "$(json_info | tr -d '\n')" --wait-finish
}

cmd_build() {
    [ "$VERSION" = "$IMAGE_TAG" ] || { echo "info.xml <version> ($VERSION) and <image-tag> ($IMAGE_TAG) differ; bump both."; exit 1; }
    docker build -t "$IMAGE" "$ROOT"
}

copy_info_xml() {
    docker cp "$INFO_XML" "$("${COMPOSE[@]}" ps -q nextcloud):/tmp/$APP_ID-info.xml"
}

cmd_register_docker() {
    copy_info_xml
    occ app_api:app:unregister "$APP_ID" --force --silent || true
    occ app_api:app:register "$APP_ID" "$DAEMON_HARP" --info-xml "/tmp/$APP_ID-info.xml" \
        --env "SOCKLESS_MAIL_ENCRYPTION_KEY=$ENCRYPTION_KEY" --env SOCKLESS_MAIL_ALLOW_LOCAL_SERVERS=true --wait-finish
}

cmd_update_docker() {
    copy_info_xml
    occ app_api:app:update "$APP_ID" --info-xml "/tmp/$APP_ID-info.xml" --wait-finish
    # Observed with Nextcloud 35 / current HaRP: after app:update HaRP keeps serving cached
    # metadata without the app's routes (every /exapps/ path answers 404) although the routes
    # are in Nextcloud's database. Disabling and enabling makes AppAPI push them to HaRP again.
    occ app_api:app:disable "$APP_ID"
    occ app_api:app:enable "$APP_ID"
}

# Assistant (phase 21): integration_openai as the task provider, answered by dev/ai-stub.mjs,
# with dev/docker-compose.yml's taskworker running tasks at once. Dev-only settings.
cmd_ai() {
    occ app:install integration_openai >/dev/null 2>&1 || occ app:enable integration_openai >/dev/null
    # The stub lives on the compose network, which Nextcloud's HTTP client refuses by default.
    occ config:system:set allow_local_remote_servers --value=true --type=boolean
    occ config:app:set integration_openai url --value=http://ai-stub:8000/v1 --lazy
    occ config:app:set integration_openai api_key --value=dev-only-stub-key --lazy
    occ config:app:set integration_openai default_completion_model_id --value=stub-model --lazy
    occ config:app:set integration_openai chat_endpoint_enabled --value=1 --lazy
    occ config:app:set integration_openai llm_provider_enabled --value=1 --lazy
    # The worker caches app config for its lifetime.
    "${COMPOSE[@]}" restart taskworker >/dev/null
    echo "Assistant tasks now run against the stub (http://ai-stub:8000/v1)."
}

cmd_help() {
    cat <<EOF
Usage: dev/dev.sh <command>

  up               Start Nextcloud + HaRP + GreenMail, register both deploy daemons
  ai               Make Assistant tasks work: integration_openai against the AI stub
  down             Stop the stack (data volumes are kept)

  Fast loop (app runs on your machine, manual-install daemon):
  run              Build the frontend, then run the app on port $APP_PORT (keep it running)
  register-manual  Register the running app with Nextcloud (replaces any installed copy)

  Production-like loop (container deployed by HaRP):
  build            docker build $IMAGE
  register-docker  Register the image through the HaRP daemon (replaces any installed copy)
  update-docker    Redeploy after bumping <version> and <image-tag> in appinfo/info.xml

  frontend         Build the frontend bundle only
  unregister       Remove the app from Nextcloud (data volume is kept)
  logs             Follow the ExApp container's logs
  occ <args>       Run occ in the Nextcloud container
  seed-mail [addr] Send test mail to GreenMail (default alice@example.test)
EOF
}

case "${1:-help}" in
    up) cmd_up ;;
    ai) cmd_ai ;;
    down) "${COMPOSE[@]}" down ;;
    run) cmd_run ;;
    frontend) cmd_frontend ;;
    register-manual) cmd_register_manual ;;
    build) cmd_build ;;
    register-docker) cmd_register_docker ;;
    update-docker) cmd_update_docker ;;
    unregister) occ app_api:app:unregister "$APP_ID" --force ;;
    logs) docker logs -f "nc_app_$APP_ID" ;;
    seed-mail) shift; bash "$ROOT/dev/seed-mail.sh" "$@" ;;
    occ) shift; occ "$@" ;;
    *) cmd_help ;;
esac
