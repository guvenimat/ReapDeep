FROM mcr.microsoft.com/dotnet/sdk:10.0-noble-aot AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY ReapDeep.ServiceDefaults/ReapDeep.ServiceDefaults.csproj ReapDeep.ServiceDefaults/
COPY ReapDeep.Ingestion/ReapDeep.Ingestion.csproj ReapDeep.Ingestion/
RUN dotnet restore ReapDeep.Ingestion/ReapDeep.Ingestion.csproj -r linux-x64
COPY ReapDeep.ServiceDefaults/ ReapDeep.ServiceDefaults/
COPY ReapDeep.Ingestion/ ReapDeep.Ingestion/
RUN dotnet publish ReapDeep.Ingestion/ReapDeep.Ingestion.csproj -c Release -r linux-x64 --no-restore -o /app

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8000
EXPOSE 8000
USER app
ENTRYPOINT ["./ReapDeep.Ingestion"]
