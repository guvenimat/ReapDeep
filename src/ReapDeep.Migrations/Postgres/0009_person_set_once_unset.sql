CREATE OR REPLACE FUNCTION person_set_once_keys(props JSONB, times JSONB, changes JSONB, ts BIGINT) RETURNS TABLE (key TEXT, value JSONB)
LANGUAGE sql IMMUTABLE AS $$
    SELECT c.key, c.value FROM jsonb_each(COALESCE(changes, '{}')) c
    WHERE (NOT (props ? c.key) AND abs(COALESCE((times ->> c.key)::BIGINT, 0)) <= ts)
       OR ((props ? c.key) AND COALESCE((times ->> c.key)::BIGINT, 0) < 0 AND -(times ->> c.key)::BIGINT > ts)
$$;

CREATE OR REPLACE FUNCTION person_props_set_once(props JSONB, times JSONB, changes JSONB, ts BIGINT) RETURNS JSONB
LANGUAGE sql IMMUTABLE AS $$
    SELECT props || COALESCE((SELECT jsonb_object_agg(k.key, k.value) FROM person_set_once_keys(props, times, changes, ts) k), '{}')
$$;

CREATE OR REPLACE FUNCTION person_times_set_once(props JSONB, times JSONB, changes JSONB, ts BIGINT) RETURNS JSONB
LANGUAGE sql IMMUTABLE AS $$
    SELECT times || COALESCE((SELECT jsonb_object_agg(k.key, to_jsonb(-ts)) FROM person_set_once_keys(props, times, changes, ts) k), '{}')
$$;

CREATE OR REPLACE FUNCTION person_props_unset(props JSONB, times JSONB, keys JSONB, ts BIGINT) RETURNS JSONB
LANGUAGE sql IMMUTABLE AS $$
    SELECT props - COALESCE(ARRAY(
        SELECT k FROM jsonb_array_elements_text(COALESCE(keys, '[]')) k
        WHERE abs(COALESCE((times ->> k)::BIGINT, 0)) <= ts), '{}')
$$;

CREATE OR REPLACE FUNCTION person_times_unset(times JSONB, keys JSONB, ts BIGINT) RETURNS JSONB
LANGUAGE sql IMMUTABLE AS $$
    SELECT times || COALESCE((
        SELECT jsonb_object_agg(k, to_jsonb(ts)) FROM jsonb_array_elements_text(COALESCE(keys, '[]')) k
        WHERE abs(COALESCE((times ->> k)::BIGINT, 0)) <= ts), '{}')
$$;

CREATE OR REPLACE FUNCTION person_props_apply(props JSONB, times JSONB, s JSONB, so JSONB, u JSONB, ts BIGINT) RETURNS JSONB
LANGUAGE sql IMMUTABLE AS $$
    SELECT person_props_unset(
        person_props_set(person_props_set_once(props, times, so, ts), person_times_set_once(props, times, so, ts), s, ts),
        person_times_set(person_times_set_once(props, times, so, ts), s, ts), u, ts)
$$;

CREATE OR REPLACE FUNCTION person_times_apply(props JSONB, times JSONB, s JSONB, so JSONB, u JSONB, ts BIGINT) RETURNS JSONB
LANGUAGE sql IMMUTABLE AS $$
    SELECT person_times_unset(person_times_set(person_times_set_once(props, times, so, ts), s, ts), u, ts)
$$;

CREATE OR REPLACE FUNCTION person_props_merge(props_from JSONB, props_into JSONB, times_into JSONB) RETURNS JSONB
LANGUAGE sql IMMUTABLE AS $$
    SELECT (props_from - COALESCE(ARRAY(
        SELECT t.key FROM jsonb_each(times_into) t WHERE NOT (props_into ? t.key)), '{}')) || props_into
$$;
