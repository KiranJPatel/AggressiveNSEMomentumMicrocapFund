# Optional (spec §10.2 lists Docker as optional for Tranche 21) — like
# .github/workflows/ci.yml, this has never actually been built or run in
# this sandbox (no Docker daemon, no network to pull base images here).
# A careful hand-trace of a working multi-stage build for this solution,
# not a verified one — see docs/gap-log.md.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# No NuGet packages anywhere in this solution (see docs/gap-log.md) — every
# Microsoft.Extensions.* dependency comes from the Microsoft.AspNetCore.App
# shared framework via <FrameworkReference>, so `dotnet restore` here is a
# near-instant no-op rather than a real package download.
COPY . .
RUN dotnet restore TradingPlatform.sln
RUN dotnet publish src/TradingPlatform.Application/TradingPlatform.Application.csproj \
    --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# Runtime output directories the app writes to at startup (SchemaInitializer
# creates the SQLite file under data/ on first run) — see PathsOptions.
RUN mkdir -p data logs reports backtests exports config
COPY config/appsettings.json config/appsettings.json

# `serve`'s default bind address (ASPNETCORE_URLS, standard ASP.NET Core
# convention — see ServeCommand's remarks) inside the container.
# Binding 0.0.0.0 means `serve` REFUSES to start unless TRADING_API_KEY is set (or --allow-unauthenticated is passed) — see README > Security.
# Run with:  docker run -e TRADING_API_KEY=... -p 127.0.0.1:5000:5000 ...
ENV ASPNETCORE_URLS=http://0.0.0.0:5000
EXPOSE 5000

# No default CMD args: the image supports every CLI command (env, fetch,
# scan, backtest, optimize, walkforward, montecarlo, paper, live,
# portfolio, reconcile, serve) — the operator picks one per `docker run`,
# e.g. `docker run <image> serve` or `docker run <image> env`.
ENTRYPOINT ["dotnet", "TradingPlatform.Application.dll"]
