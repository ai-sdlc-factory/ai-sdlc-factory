#!/bin/sh
# Entrypoint of every sandbox: prepares OpenCode's settings and login, then runs the command.
set -e

# The launcher points HOME and the XDG directories into the container's tmpfs but does not create them. Most tools
# create what they need; the .NET CLI crashes instead, which made every .NET project check fail.
mkdir -p "${HOME:-/tmp/home}" "${XDG_CACHE_HOME:-/tmp/cache}" "${XDG_CONFIG_HOME:-/tmp/config}"

# OpenCode writes into its config directory, so give it a writable copy of the shared settings
# (agents/ and opencode.json from the factory's agents folder, mounted read-only at /etc/factory).
if [ -d /etc/factory ]; then
    mkdir -p "$OPENCODE_CONFIG_DIR"
    cp -r /etc/factory/. "$OPENCODE_CONFIG_DIR/"
    if [ -f "$OPENCODE_CONFIG_DIR/opencode.json" ]; then
        export OPENCODE_CONFIG="$OPENCODE_CONFIG_DIR/opencode.json"
    fi
fi

# The login volume (mounted only into runs that need the model) holds auth.json. Only that file is
# linked into OpenCode's per-run data directory, so sessions and logs of different runs never mix.
if [ -f /auth/auth.json ]; then
    mkdir -p "$XDG_DATA_HOME/opencode"
    ln -sf /auth/auth.json "$XDG_DATA_HOME/opencode/auth.json"
elif [ "${FACTORY_REQUIRE_AUTH:-}" = "1" ]; then
    echo "OpenCode is not signed in: no login found. Run the opencode-login Compose service first." >&2
    exit 78
fi

exec "$@"
