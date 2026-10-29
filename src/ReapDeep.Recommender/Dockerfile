FROM mcr.microsoft.com/dotnet/sdk:10.0-noble-aot AS build
WORKDIR /src
COPY ReapDeep.Analytics/ReapDeep.Analytics.csproj ReapDeep.Analytics/
COPY ReapDeep.Recommender/ReapDeep.Recommender.csproj ReapDeep.Recommender/
RUN dotnet restore ReapDeep.Recommender/ReapDeep.Recommender.csproj -r linux-x64
COPY ReapDeep.Analytics/ ReapDeep.Analytics/
COPY ReapDeep.Recommender/ ReapDeep.Recommender/
RUN dotnet publish ReapDeep.Recommender/ReapDeep.Recommender.csproj -c Release -r linux-x64 --no-restore -o /app

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8002
EXPOSE 8002
USER app
ENTRYPOINT ["./ReapDeep.Recommender"]
