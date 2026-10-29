using ReapDeep.QueryApi.Auth;

namespace ReapDeep.QueryApi.Organization;

public enum MembershipError
{
    None,
    NotFound,
    OwnerOnly,
    LastOwner,
}

public static class MembershipRules
{
    public static MembershipError CanAdd(string actorRole, string newRole) =>
        newRole == Roles.Owner && actorRole != Roles.Owner ? MembershipError.OwnerOnly : MembershipError.None;

    public static MembershipError CanChange(string actorRole, IReadOnlyDictionary<long, string> roles, long userId, string? newRole)
    {
        if (!roles.TryGetValue(userId, out var current)) return MembershipError.NotFound;
        if (actorRole != Roles.Owner && (current == Roles.Owner || newRole == Roles.Owner)) return MembershipError.OwnerOnly;
        if (current == Roles.Owner && newRole != Roles.Owner && roles.Values.Count(r => r == Roles.Owner) == 1) return MembershipError.LastOwner;
        return MembershipError.None;
    }
}
