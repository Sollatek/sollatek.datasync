#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "Usage: $0 <provider> [runtime] [output] [configuration] [monitoring-provider]" >&2
  echo "Providers: all, sql, sqlserver, mssql, postgres, mysql, mongo, mongodb, filesystem, files" >&2
  echo "Monitoring providers: none, all, otlp, azuremonitor, status, otlp-status, azuremonitor-status, otlp-azuremonitor, otlp-azuremonitor-status" >&2
}

normalize_provider() {
  value="$(printf '%s' "$1" | tr '[:upper:]' '[:lower:]')"
  case "$value" in
    mssql) echo "sqlserver" ;;
    mongodb) echo "mongo" ;;
    files) echo "filesystem" ;;
    *) echo "$value" ;;
  esac
}

normalize_monitoring_provider() {
  value="$(printf '%s' "$1" | tr '[:upper:]' '[:lower:]')"
  case "$value" in
    opentelemetry) echo "otlp" ;;
    azure|appinsights|applicationinsights) echo "azuremonitor" ;;
    statusendpoint|http) echo "status" ;;
    *) echo "$value" ;;
  esac
}

default_runtime() {
  case "$(uname -m)" in
    aarch64|arm64) echo "linux-arm64" ;;
    *) echo "linux-x64" ;;
  esac
}

if [[ $# -lt 1 ]]; then
  usage
  exit 2
fi

if ! command -v docker >/dev/null 2>&1; then
  echo "Docker is required to publish DataSync without a local .NET SDK." >&2
  exit 1
fi

provider="$(normalize_provider "$1")"
runtime="${2:-$(default_runtime)}"
monitoring_provider="$(normalize_monitoring_provider "${5:-none}")"
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/.." && pwd)"
if [[ -n "${3:-}" ]]; then
  output="$3"
elif [[ "$monitoring_provider" == "none" ]]; then
  output="$repo_root/.artifacts/publish/datasync-$provider-$runtime"
else
  output="$repo_root/.artifacts/publish/datasync-$provider-$monitoring_provider-$runtime"
fi
configuration="${4:-Release}"
image_tag="sollatek-datasync-publish:$provider-$monitoring_provider-$runtime-$(date +%s)-$$"
container_id=""
stage="$repo_root/.artifacts/publish/.tmp-$provider-$monitoring_provider-$runtime-$(date +%s)-$$"

case "$provider" in
  all|sql|sqlserver|postgres|mysql|mongo|filesystem) ;;
  *)
    usage
    exit 2
    ;;
esac

case "$monitoring_provider" in
  none|all|otlp|azuremonitor|status|otlp-status|azuremonitor-status|otlp-azuremonitor|otlp-azuremonitor-status) ;;
  *)
    usage
    exit 2
    ;;
esac

cleanup() {
  if [[ -n "$container_id" ]]; then
    docker rm "$container_id" >/dev/null 2>&1 || true
  fi

  docker rmi "$image_tag" >/dev/null 2>&1 || true

  rm -rf "$stage"
}
trap cleanup EXIT

docker build \
  --file "$script_dir/Dockerfile.publish" \
  --target export \
  --build-arg "DATASYNC_PROVIDER=$provider" \
  --build-arg "DATASYNC_MONITORING_PROVIDER=$monitoring_provider" \
  --build-arg "RUNTIME_IDENTIFIER=$runtime" \
  --build-arg "CONFIGURATION=$configuration" \
  --tag "$image_tag" \
  "$repo_root"

container_id="$(docker create "$image_tag")"
mkdir -p "$stage"
docker cp "$container_id:/out/." "$stage"
mkdir -p "$(dirname "$output")"
rm -rf "$output"
mv "$stage" "$output"

echo "Published DataSync provider '$provider' with monitoring '$monitoring_provider' for runtime '$runtime' to '$output'."
