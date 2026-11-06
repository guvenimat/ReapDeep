using ReapDeep.PersonWorker.Identity;
using ReapDeep.PersonWorker.Infrastructure;

if (PersonResync.TryParseArgs(args, out var projectId))
    return await PersonResync.RunAsync(Host.CreateApplicationBuilder().Configuration.GetConnectionString("Postgres"), projectId);

var builder = Host.CreateApplicationBuilder(args);
builder.AddPersonWorker();
builder.Build().Run();
return 0;
