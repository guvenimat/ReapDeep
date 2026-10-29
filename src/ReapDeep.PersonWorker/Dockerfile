FROM mcr.microsoft.com/dotnet/sdk:10.0-noble-aot AS build
WORKDIR /src
COPY ReapDeep.Analytics/ReapDeep.Analytics.csproj ReapDeep.Analytics/
COPY ReapDeep.PersonWorker/ReapDeep.PersonWorker.csproj ReapDeep.PersonWorker/
RUN dotnet restore ReapDeep.PersonWorker/ReapDeep.PersonWorker.csproj -r linux-x64
COPY ReapDeep.Analytics/ ReapDeep.Analytics/
COPY ReapDeep.PersonWorker/ ReapDeep.PersonWorker/
RUN dotnet publish ReapDeep.PersonWorker/ReapDeep.PersonWorker.csproj -c Release -r linux-x64 --no-restore -o /app

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble
WORKDIR /app
COPY --from=build /app .
USER app
ENTRYPOINT ["./ReapDeep.PersonWorker"]
