using Npgsql;

namespace ReapDeep.PersonWorker.Identity;

public sealed class IdentityResolver(NpgsqlDataSource db, ILogger<IdentityResolver> log)
{
    public async Task ProcessAsync(IdentityEvent e, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var persons = new PersonTransaction(conn, tx, e.ProjectId);

        await persons.LockAsync(e.DistinctId, e.AnonId);
        if (e.IsIdentify) await IdentifyAsync(persons, e.DistinctId, e.AnonId, e.SetJson);
        else if (e.SetJson is not null) await SetAsync(persons, e.DistinctId, e.SetJson);

        await persons.WriteOutboxAsync();
        await tx.CommitAsync(ct);
    }

    async Task IdentifyAsync(PersonTransaction t, string user, string? anon, string? set)
    {
        var pUser = await t.FindAsync(user);
        if (anon is null)
        {
            if (pUser is { } u) await t.UpdateAsync(u.Id, set, identify: true);
            else await t.LinkAsync(user, await t.CreateAsync(identified: true, set));
            return;
        }

        var pAnon = await t.FindAsync(anon);
        switch (pUser, pAnon)
        {
            case (null, null):
                var p = await t.CreateAsync(identified: true, set);
                await t.LinkAsync(anon, p);
                await t.LinkAsync(user, p);
                break;

            case ({ } u, null):
                await t.LinkAsync(anon, u.Id);
                await t.UpdateAsync(u.Id, set, identify: true);
                break;

            case (null, { IsIdentified: false } a):
                await t.LinkAsync(user, a.Id);
                await t.UpdateAsync(a.Id, set, identify: true);
                break;

            case (null, { } a):
                log.LogWarning("Project {Project}: {Anon} already belongs to identified person {Person}; created a new person for {User}",
                    t.ProjectId, anon, a.Id, user);
                await t.LinkAsync(user, await t.CreateAsync(identified: true, set));
                break;

            case ({ } u, { } a) when u.Id == a.Id:
                await t.UpdateAsync(u.Id, set, identify: true);
                break;

            case ({ } u, { IsIdentified: false } a):
                await t.MergeAsync(from: a.Id, into: u.Id, set);
                break;

            case ({ } u, { } a):
                log.LogWarning("Project {Project}: {User} ({UserPerson}) and {Anon} ({AnonPerson}) are both identified; merge rejected",
                    t.ProjectId, user, u.Id, anon, a.Id);
                await t.UpdateAsync(u.Id, set, identify: true);
                break;
        }
    }

    static async Task SetAsync(PersonTransaction t, string distinctId, string set)
    {
        if (await t.FindAsync(distinctId) is { } p) await t.UpdateAsync(p.Id, set, identify: false);
        else await t.LinkAsync(distinctId, await t.CreateAsync(identified: false, set));
    }
}
