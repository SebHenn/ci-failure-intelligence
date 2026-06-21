# syntax=docker/dockerfile:1

# cifail Docker image — the FULL build: includes every external database provider
# (PostgreSQL, MySQL/MariaDB, SQL Server, MongoDB) via -p:IncludeExternalDb=true, plus
# git so the R3 auto-resolution features work against a mounted repository.

# ---- build ----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Restore against just the project files first so layers cache when only code changes.
COPY src/CiFail.Core/CiFail.Core.csproj         src/CiFail.Core/
COPY src/CiFail.Cli/CiFail.Cli.csproj           src/CiFail.Cli/
COPY src/CiFail.Providers/CiFail.Providers.csproj src/CiFail.Providers/
RUN dotnet restore src/CiFail.Cli/CiFail.Cli.csproj -p:IncludeExternalDb=true

COPY . .
RUN dotnet publish src/CiFail.Cli/CiFail.Cli.csproj \
        -c Release \
        -p:IncludeExternalDb=true \
        --no-restore \
        -o /app

# ---- runtime --------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime

# git powers R3 (correlating failures with the commit that fixed them). safe.directory '*'
# avoids "dubious ownership" errors when the mounted repo is owned by a different user.
RUN apt-get update \
    && apt-get install -y --no-install-recommends git \
    && rm -rf /var/lib/apt/lists/* \
    && git config --system --add safe.directory '*'

COPY --from=build /app /app

# Mount your project at /work; history persists under /data (mount a volume to keep it).
ENV CIFAIL_HOME=/data
RUN mkdir -p /data
WORKDIR /work

ENTRYPOINT ["/app/cifail"]
CMD ["--help"]
