using ReapDeep.QueryApi.Actions;
using ReapDeep.QueryApi.Alerts;
using ReapDeep.QueryApi.Annotations;
using ReapDeep.QueryApi.Analytics;
using ReapDeep.QueryApi.Auth;
using ReapDeep.QueryApi.Catalog;
using ReapDeep.QueryApi.Cohorts;
using ReapDeep.QueryApi.Deletion;
using ReapDeep.QueryApi.Experiments;
using ReapDeep.QueryApi.Flags;
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
        api.MapAnnotations();
        api.MapActions();
        api.MapDashboards();
        api.MapMembers();
        api.MapApiKeys();
        api.MapProjects();
        api.MapDeletionRequests();
        api.MapCohorts();
        api.MapCatalog();
        api.MapAlerts();
        api.MapFlags();
        api.MapExperiments();
    }
}
