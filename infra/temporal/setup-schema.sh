#!/bin/sh
set -eu

# Databases already exist and belong to the non-superuser temporal account.
# setup-schema and update-schema can be rerun against an initialized database.
for database in temporal temporal_visibility; do
    case "$database" in
        temporal) schema=temporal ;;
        temporal_visibility) schema=visibility ;;
    esac

    temporal-sql-tool --plugin postgres12 --ep postgres -p 5432 \
        -u temporal --db "$database" setup-schema -v 0.0
    temporal-sql-tool --plugin postgres12 --ep postgres -p 5432 \
        -u temporal --db "$database" update-schema \
        -d "/etc/temporal/schema/postgresql/v12/$schema/versioned"
done
