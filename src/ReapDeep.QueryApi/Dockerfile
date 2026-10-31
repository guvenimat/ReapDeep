FROM mcr.microsoft.com/dotnet/sdk:10.0-noble-aot AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY ReapDeep.Analytics/ReapDeep.Analytics.csproj ReapDeep.Analytics/
COPY ReapDeep.ServiceDefaults/ReapDeep.ServiceDefaults.csproj ReapDeep.ServiceDefaults/
COPY ReapDeep.QueryApi/ReapDeep.QueryApi.csproj ReapDeep.QueryApi/
RUN dotnet restore ReapDeep.QueryApi/ReapDeep.QueryApi.csproj -r linux-x64
COPY ReapDeep.Analytics/ ReapDeep.Analytics/
COPY ReapDeep.ServiceDefaults/ ReapDeep.ServiceDefaults/
COPY ReapDeep.QueryApi/ ReapDeep.QueryApi/
RUN dotnet publish ReapDeep.QueryApi/ReapDeep.QueryApi.csproj -c Release -r linux-x64 --no-restore -o /app

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER app
ENTRYPOINT ["./ReapDeep.QueryApi"]
