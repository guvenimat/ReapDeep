using ReapDeep.Ingestion.Infrastructure;

var builder = WebApplication.CreateSlimBuilder(args);
builder.AddIngestion();

var app = builder.Build();
app.UseIngestion();
app.Run();
