#!/bin/sh
set -e
psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" -v key="$READ_API_KEY" <<'SQL'
INSERT INTO api_keys (project_id, name, key_prefix, key_hash)
VALUES (1, 'Local read key', left(:'key', 6), encode(sha256(convert_to(:'key', 'UTF8')), 'hex'));
SQL
