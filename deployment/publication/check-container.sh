#!/usr/bin/env bash
set -euo pipefail

image=${1:?image required}
root=$(mktemp -d "${RUNNER_TEMP:-/tmp}/datasync-smoke.XXXXXX")
network=''
schema=''
worker=''
cleanup() {
    if [ -n "$worker" ]; then docker rm -f -v "$worker" >/dev/null; fi
    if [ -n "$schema" ]; then docker rm -f "$schema" >/dev/null; fi
    if [ -n "$network" ]; then docker network rm "$network" >/dev/null; fi
    rm -r -- "$root"
}
trap cleanup EXIT
mkdir "$root/schema"
cat > "$root/schema/swagger.json" <<'JSON'
{
  "openapi": "3.0.1",
  "paths": {},
  "components": {
    "schemas": {
      "Asset": {
        "type": "object",
        "properties": {
          "id": { "type": "string", "format": "uuid" }
        }
      }
    }
  },
  "x-sollatek-sync": {
    "version": 1,
    "entities": {
      "assets": {
        "schema": "#/components/schemas/Asset",
        "operationId": "Assets_Get",
        "table": "assets",
        "collection": "assets",
        "primaryKey": ["id"]
      }
    }
  }
}
JSON
jq '.SyncPlan = ["assets"]' deployment/publication/appsettings.public.json > "$root/appsettings.json"
chmod 0444 "$root/appsettings.json"
network=$(docker network create --internal "datasync-${root##*.}")
schema=$(docker run -d --network "$network" --network-alias schema --read-only --cap-drop ALL \
    --mount "type=bind,source=$root/schema,target=/schema,readonly" \
    busybox:1.37.0@sha256:a7dbc319b1438115a207e11930dc4bb3e643dcb3ec0bdd694bb36cd8fad289ec \
    httpd -f -p 8080 -h /schema)
worker=$(docker run -d --network "$network" --read-only --tmpfs /tmp --cap-drop ALL \
    --security-opt no-new-privileges \
    --mount "type=bind,source=$root/appsettings.json,target=/app/appsettings.json,readonly" \
    --env Settings__apiUrl=http://schema:8080 --env Settings__oauthUrl=https://127.0.0.1:1/ \
    --env Settings__clientKey=image-check --env Settings__clientSecret=fixture-only \
    --env SwaggerDocuments__0__name=data-v1 --env SwaggerDocuments__0__url=http://schema:8080/swagger.json \
    --env Storage__provider=filesystem --env State__provider=filesystem --env FileExport__rootPath=/tmp/exports \
    --env Sync__runOnStartup=false --env Sync__schedule__mode=fixedDelay --env Sync__stopWhenFinished=false \
    "$image")
for ((attempt = 0; attempt < 30; ++attempt)); do
    if [ "$(docker inspect --format '{{.State.Running}}' "$worker")" != true ]; then break; fi
    log=$(docker logs "$worker" 2>&1)
    if grep -Fq 'Loaded swagger sync metadata for 1 selected entities: assets' <<<"$log" &&
        grep -Fq 'Application started.' <<<"$log"; then
        echo 'DataSync started with the filesystem provider and loaded the isolated Swagger fixture.'
        exit 0
    fi
    sleep 1
done
docker logs --tail 100 "$worker" >&2
echo 'DataSync did not start with the isolated release fixture.' >&2
exit 1
