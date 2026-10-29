using ReapDeep.QueryApi.Infrastructure;

var builder = WebApplication.CreateSlimBuilder(args);
builder.AddQueryApi();

var app = builder.Build();
app.UseQueryApi();
app.Run();
