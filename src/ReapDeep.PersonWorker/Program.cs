using Npgsql;
using ReapDeep.PersonWorker;

var builder = Host.CreateApplicationBuilder(args);

var dataSource = new NpgsqlSlimDataSourceBuilder(builder.Configuration.GetConnectionString("Postgres"));
dataSource.EnableArrays();
builder.Services.AddSingleton(dataSource.Build());

builder.Services.AddSingleton<IdentityResolver>();
builder.Services.AddHostedService<EventConsumer>();
builder.Services.AddHostedService<OutboxPublisher>();
builder.Services.AddHostedService<DeletionJob>();

builder.Build().Run();
