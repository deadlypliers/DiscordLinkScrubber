FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so this layer is cached until a project file changes.
COPY LinkCleaner.slnx ./
COPY src/LinkCleaner.Core/LinkCleaner.Core.csproj src/LinkCleaner.Core/
COPY src/LinkCleaner.Bot/LinkCleaner.Bot.csproj src/LinkCleaner.Bot/
RUN dotnet restore src/LinkCleaner.Bot/LinkCleaner.Bot.csproj

COPY src/ src/
RUN dotnet publish src/LinkCleaner.Bot/LinkCleaner.Bot.csproj -c Release --no-restore -o /out

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /out .

# Config database lives on a volume so it survives rebuilds and upgrades.
ENV LINKCLEANER_DB=/data/linkcleaner.db
RUN mkdir /data && chown $APP_UID /data
VOLUME /data

USER $APP_UID
ENTRYPOINT ["dotnet", "LinkCleaner.Bot.dll"]
