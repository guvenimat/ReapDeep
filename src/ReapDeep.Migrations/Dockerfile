FROM mcr.microsoft.com/dotnet/sdk:10.0-noble-aot AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY ReapDeep.Migrations/ReapDeep.Migrations.csproj ReapDeep.Migrations/
RUN dotnet restore ReapDeep.Migrations/ReapDeep.Migrations.csproj -r linux-x64
COPY ReapDeep.Migrations/ ReapDeep.Migrations/
RUN dotnet publish ReapDeep.Migrations/ReapDeep.Migrations.csproj -c Release -r linux-x64 --no-restore -o /app

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble
WORKDIR /app
COPY --from=build /app .
USER app
ENTRYPOINT ["./ReapDeep.Migrations"]
