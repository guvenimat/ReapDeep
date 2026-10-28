FROM mcr.microsoft.com/dotnet/sdk:10.0-noble-aot AS build
WORKDIR /src
COPY ReapDeep.Ingestion.csproj .
RUN dotnet restore -r linux-x64
COPY . .
RUN dotnet publish -c Release -r linux-x64 --no-restore -o /app

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8000
EXPOSE 8000
USER app
ENTRYPOINT ["./ReapDeep.Ingestion"]
