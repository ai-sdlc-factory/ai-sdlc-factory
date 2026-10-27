#!/bin/bash
# The official PostgreSQL entrypoint runs this only on an empty data volume.
set -e

psql --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
    --set=ON_ERROR_STOP=1 \
    --set=forgejo_password="$FORGEJO_DB_PASSWORD" \
    --set=temporal_password="$TEMPORAL_DB_PASSWORD" <<'SQL'
CREATE ROLE forgejo LOGIN PASSWORD :'forgejo_password';
CREATE ROLE temporal LOGIN PASSWORD :'temporal_password';

CREATE DATABASE forgejo OWNER forgejo;
CREATE DATABASE temporal OWNER temporal;
CREATE DATABASE temporal_visibility OWNER temporal;

REVOKE ALL ON DATABASE forgejo FROM PUBLIC;
REVOKE ALL ON DATABASE temporal FROM PUBLIC;
REVOKE ALL ON DATABASE temporal_visibility FROM PUBLIC;
SQL
