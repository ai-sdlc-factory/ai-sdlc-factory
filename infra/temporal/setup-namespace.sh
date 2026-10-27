#!/bin/sh
set -eu

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

if temporal operator namespace describe --namespace default >/dev/null 2>&1; then
    echo "Temporal namespace 'default' already exists."
else
    temporal operator namespace create --namespace default --retention 7d
fi
