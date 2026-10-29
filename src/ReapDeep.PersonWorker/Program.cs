using ReapDeep.PersonWorker.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);
builder.AddPersonWorker();
builder.Build().Run();
