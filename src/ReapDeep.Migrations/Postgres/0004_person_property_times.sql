ALTER TABLE persons ADD COLUMN property_times JSONB NOT NULL DEFAULT '{}';

CREATE OR REPLACE FUNCTION person_props_set(props JSONB, times JSONB, changes JSONB, ts BIGINT) RETURNS JSONB
LANGUAGE sql IMMUTABLE AS $$
    SELECT props || COALESCE((
        SELECT jsonb_object_agg(c.key, c.value) FROM jsonb_each(COALESCE(changes, '{}')) c
        WHERE COALESCE((times ->> c.key)::BIGINT, -1) <= ts), '{}')
$$;

CREATE OR REPLACE FUNCTION person_times_set(times JSONB, changes JSONB, ts BIGINT) RETURNS JSONB
LANGUAGE sql IMMUTABLE AS $$
    SELECT times || COALESCE((
        SELECT jsonb_object_agg(c.key, to_jsonb(ts)) FROM jsonb_each(COALESCE(changes, '{}')) c
        WHERE COALESCE((times ->> c.key)::BIGINT, -1) <= ts), '{}')
$$;

CREATE OR REPLACE FUNCTION person_times_merge(times_from JSONB, props_into JSONB, times_into JSONB) RETURNS JSONB
LANGUAGE sql IMMUTABLE AS $$
    SELECT COALESCE((
        SELECT jsonb_object_agg(f.key, f.value) FROM jsonb_each(times_from) f
        WHERE NOT (props_into ? f.key)), '{}') || times_into
$$;
