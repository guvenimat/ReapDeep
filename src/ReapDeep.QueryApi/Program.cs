using System.Text.Json;
using MySqlConnector;
using Npgsql;
using ReapDeep.QueryApi;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
});
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var pg = new NpgsqlSlimDataSourceBuilder(builder.Configuration.GetConnectionString("Postgres"));
pg.EnableArrays();
builder.Services.AddSingleton(pg.Build());

builder.Services.AddSingleton(new MySqlDataSource(builder.Configuration.GetConnectionString("StarRocks")!));

builder.Services.AddSingleton<ApiKeyAuth>();

var app = builder.Build();
app.UseCors();
app.MapQueryApi();
app.Run();
