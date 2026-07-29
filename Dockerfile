# syntax=docker/dockerfile:1

# cifail Docker image — the FULL build: includes every external database provider
# (PostgreSQL, MySQL/MariaDB, SQL Server, MongoDB) via -p:IncludeExternalDb=true, the
# `cifail serve` HTTP API (ASP.NET Core), plus git so the R3 auto-resolution features work
# against a mounted repository.

# ---- build ----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Restore against just the project files first so layers cache when only code changes.
# Directory.Build.props must come along: it carries TargetFramework and Version for every
# project, and without it restore fails with NETSDK1013 (TargetFramework value '').
COPY Directory.Build.props                      ./
COPY src/CiFail.Core/CiFail.Core.csproj         src/CiFail.Core/
COPY src/CiFail.Cli/CiFail.Cli.csproj           src/CiFail.Cli/
COPY src/CiFail.Providers/CiFail.Providers.csproj src/CiFail.Providers/
COPY src/CiFail.Server/CiFail.Server.csproj     src/CiFail.Server/
RUN dotnet restore src/CiFail.Cli/CiFail.Cli.csproj -p:IncludeExternalDb=true

COPY . .
RUN dotnet publish src/CiFail.Cli/CiFail.Cli.csproj \
        -c Release \
        -p:IncludeExternalDb=true \
        --no-restore \
        -o /app

# ---- runtime --------------------------------------------------------------
# ASP.NET Core runtime (not the plain runtime) so `cifail serve` has the shared framework.
# CLI-only commands run on it just the same.
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

# git powers R3 (correlating failures with the commit that fixed them). safe.directory '*'
# avoids "dubious ownership" errors when the mounted repo is owned by a different user.
RUN apt-get update \
    && apt-get install -y --no-install-recommends git \
    && rm -rf /var/lib/apt/lists/* \
    && git config --system --add safe.directory '*'

COPY --from=build /app /app

# Put cifail on PATH so it works both as the entrypoint (`docker run … analyze x.log`) and
# when a CI runner overrides the entrypoint and calls `cifail` from a script (e.g. GitLab).
RUN ln -s /app/cifail /usr/local/bin/cifail

# Mount your project at /work; history persists under /data (mount a volume to keep it).
ENV CIFAIL_HOME=/data
RUN mkdir -p /data
WORKDIR /work

# `cifail serve` listens here by default (shared-service mode; see deploy/).
EXPOSE 8080

ENTRYPOINT ["/app/cifail"]
CMD ["--help"]
