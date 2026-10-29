using Npgsql;
using NpgsqlTypes;

namespace ReapDeep.PersonWorker;

public sealed class IdentityResolver(NpgsqlDataSource db, ILogger<IdentityResolver> log)
{
    public async Task ProcessAsync(long projectId, string eventName, string distinctId, string? anonId, string? setJson, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var t = new PersonTx(conn, tx, projectId, ct);

        if (anonId == distinctId) anonId = null;
        await t.Lock(distinctId, anonId);

        if (eventName == "$identify") await Identify(t, distinctId, anonId, setJson);
        else if (setJson is not null) await Set(t, distinctId, setJson);

        await t.WriteOutbox();
        await tx.CommitAsync(ct);
    }

    async Task Identify(PersonTx t, string user, string? anon, string? set)
    {
        var pUser = await t.Find(user);

        if (anon is null)
        {
            if (pUser is { } u) await t.Update(u.Id, set, identify: true);
            else await t.Link(user, await t.Create(identified: true, set));
            return;
        }

        var pAnon = await t.Find(anon);
        switch (pUser, pAnon)
        {
            case (null, null):
                var p = await t.Create(identified: true, set);
                await t.Link(anon, p);
                await t.Link(user, p);
                break;

            case ({ } u, null):
                await t.Link(anon, u.Id);
                await t.Update(u.Id, set, identify: true);
                break;

            case (null, { Identified: false } a):
                await t.Link(user, a.Id);
                await t.Update(a.Id, set, identify: true);
                break;

            case (null, { } a):
                log.LogWarning("Project {Project}: {Anon} already belongs to identified person {Person}; created a new person for {User}", t.ProjectId, anon, a.Id, user);
                await t.Link(user, await t.Create(identified: true, set));
                break;

            case ({ } u, { } a) when u.Id == a.Id:
                await t.Update(u.Id, set, identify: true);
                break;

            case ({ } u, { Identified: false } a):
                await t.Merge(from: a.Id, into: u.Id, set);
                break;

            case ({ } u, { } a):
                log.LogWarning("Project {Project}: {User} ({UserPerson}) and {Anon} ({AnonPerson}) are both identified; merge rejected", t.ProjectId, user, u.Id, anon, a.Id);
                await t.Update(u.Id, set, identify: true);
                break;
        }
    }

    static async Task Set(PersonTx t, string distinctId, string set)
    {
        if (await t.Find(distinctId) is { } p) await t.Update(p.Id, set, identify: false);
        else await t.Link(distinctId, await t.Create(identified: false, set));
    }
}

readonly record struct Person(Guid Id, bool Identified);

sealed class PersonTx(NpgsqlConnection conn, NpgsqlTransaction tx, long projectId, CancellationToken ct)
{
    readonly HashSet<Guid> _persons = [];
    readonly HashSet<string> _distinctIds = [];

    public long ProjectId => projectId;

    public async Task Lock(params string?[] distinctIds)
    {
        foreach (var d in distinctIds.OfType<string>().Order(StringComparer.Ordinal))
            await Exec("SELECT pg_advisory_xact_lock(hashtextextended(@p::text || ':' || @d, 0))", ("d", d));
    }

    public async Task<Person?> Find(string distinctId)
    {
        await using var cmd = Cmd("""
            SELECT p.id, p.is_identified FROM person_distinct_ids d JOIN persons p ON p.id = d.person_id
            WHERE d.project_id = @p AND d.distinct_id = @d
            """, ("d", distinctId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new Person(r.GetGuid(0), r.GetBoolean(1)) : null;
    }

    public async Task<Guid> Create(bool identified, string? set)
    {
        var id = Guid.CreateVersion7();
        await Exec("""
            INSERT INTO persons (id, project_id, properties, is_identified, version)
            VALUES (@id, @p, COALESCE(@set::jsonb, '{}'), @ident, 1)
            """, ("id", id), ("set", Json(set)), ("ident", identified));
        _persons.Add(id);
        return id;
    }

    public async Task Link(string distinctId, Guid personId)
    {
        var changed = await Exec("""
            INSERT INTO person_distinct_ids (project_id, distinct_id, person_id, version)
            VALUES (@p, @d, @id, (extract(epoch FROM clock_timestamp()) * 1000000)::bigint)
            ON CONFLICT (project_id, distinct_id) DO UPDATE
              SET person_id = EXCLUDED.person_id, version = person_distinct_ids.version + 1, updated_at = now()
              WHERE person_distinct_ids.person_id <> EXCLUDED.person_id
            """, ("d", distinctId), ("id", personId));
        if (changed > 0) _distinctIds.Add(distinctId);
    }

    public async Task Update(Guid personId, string? set, bool identify)
    {
        if (set is null && !identify) return;
        var changed = await Exec("""
            UPDATE persons SET properties = properties || COALESCE(@set::jsonb, '{}'),
                               is_identified = is_identified OR @ident,
                               version = version + 1, updated_at = now()
            WHERE id = @id AND (properties || COALESCE(@set::jsonb, '{}') <> properties OR (@ident AND NOT is_identified))
            """, ("id", personId), ("set", Json(set)), ("ident", identify));
        if (changed > 0) _persons.Add(personId);
    }

    public async Task Merge(Guid from, Guid into, string? set)
    {
        await using (var cmd = Cmd("""
            UPDATE person_distinct_ids SET person_id = @into, version = version + 1, updated_at = now()
            WHERE person_id = @from RETURNING distinct_id
            """, ("from", from), ("into", into)))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) _distinctIds.Add(r.GetString(0));

        await Exec("""
            UPDATE persons SET properties = (SELECT properties FROM persons WHERE id = @from) || properties || COALESCE(@set::jsonb, '{}'),
                               is_identified = true, version = version + 1, updated_at = now()
            WHERE id = @into
            """, ("from", from), ("into", into), ("set", Json(set)));
        await Exec("UPDATE persons SET is_deleted = true, properties = '{}', version = version + 1, updated_at = now() WHERE id = @from", ("from", from));
        _persons.Add(from);
        _persons.Add(into);
    }

    public async Task WriteOutbox()
    {
        if (_persons.Count > 0)
            await Exec("""
                INSERT INTO person_outbox (topic, key, payload)
                SELECT 'person_updates', id::text, jsonb_build_object(
                    'project_id', project_id, 'person_id', id::text, 'properties', properties,
                    'is_identified', is_identified, 'is_deleted', is_deleted, 'version', version,
                    'created_at', to_char(created_at AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS.US'),
                    'updated_at', to_char(updated_at AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS.US'))
                FROM persons WHERE id = ANY(@ids)
                """, ("ids", _persons.ToArray()));

        if (_distinctIds.Count > 0)
            await Exec("""
                INSERT INTO person_outbox (topic, key, payload)
                SELECT 'person_distinct_id_updates', distinct_id, jsonb_build_object(
                    'project_id', project_id, 'distinct_id', distinct_id, 'person_id', person_id::text,
                    'version', version, 'is_deleted', false,
                    'updated_at', to_char(updated_at AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS.US'))
                FROM person_distinct_ids WHERE project_id = @p AND distinct_id = ANY(@dids)
                """, ("dids", _distinctIds.ToArray()));
    }

    static object Json(string? s) => (object?)s ?? DBNull.Value;

    NpgsqlCommand Cmd(string sql, params (string Name, object Value)[] args)
    {
        var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("p", projectId);
        foreach (var (name, value) in args)
        {
            if (name == "set") cmd.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = value });
            else cmd.Parameters.AddWithValue(name, value);
        }
        return cmd;
    }

    async Task<int> Exec(string sql, params (string Name, object Value)[] args)
    {
        await using var cmd = Cmd(sql, args);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
