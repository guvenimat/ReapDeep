using ReapDeep.QueryApi.Analytics;
using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Catalog;
using ReapDeep.QueryApi.Cohorts;
using ReapDeep.QueryApi.Deletion;
using ReapDeep.QueryApi.Health;
using ReapDeep.QueryApi.Insights;
using ReapDeep.QueryApi.Metadata;
using ReapDeep.QueryApi.Organization;
using ReapDeep.QueryApi.Persons;

namespace ReapDeep.QueryApi;

public static class ApiRoutes
{
    public static void MapQueryApi(this WebApplication app)
    {
        app.MapHealth();
        app.MapGroup("/api/v1/auth").MapAuth();

        var api = app.MapGroup("/api/v1").RequireProjectAccess();
        api.MapAnalytics();
        api.MapPersons();
        api.MapMetadata();
        api.MapInsights();
        api.MapDashboards();
        api.MapMembers();
        api.MapApiKeys();
        api.MapDeletionRequests();
        api.MapCohorts();
        api.MapCatalog();
    }
}
