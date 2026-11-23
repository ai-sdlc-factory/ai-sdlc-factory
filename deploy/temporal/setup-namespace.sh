#!/bin/sh
set -eu

namespace="${TEMPORAL_NAMESPACE:-default}"
retention="${TEMPORAL_RETENTION:-7d}"

# A listening port alone does not guarantee that the Temporal cluster is ready.
attempt=0
until temporal operator cluster health; do
    attempt=$((attempt + 1))
    if [ "$attempt" -ge 60 ]; then
        echo "Temporal did not become ready in time." >&2
        exit 1
    fi
    sleep 2
done

# Retention applies only when the namespace is created; existing namespaces are left untouched, unless TEMPORAL_UPDATE_RETENTION is set
# (the SQLite server creates "default" itself).
if temporal operator namespace describe --namespace "$namespace" >/dev/null 2>&1; then
    echo "Temporal namespace '$namespace' already exists."
    if [ -n "${TEMPORAL_UPDATE_RETENTION:-}" ]; then
        temporal operator namespace update --namespace "$namespace" --retention "$retention"
    fi
else
    temporal operator namespace create --namespace "$namespace" --retention "$retention"
fi
