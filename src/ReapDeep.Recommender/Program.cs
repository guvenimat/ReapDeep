using ReapDeep.Recommender.Infrastructure;

var builder = WebApplication.CreateSlimBuilder(args);
builder.AddRecommender();

var app = builder.Build();
app.UseRecommender();
app.Run();
