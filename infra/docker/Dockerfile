# ── Stage 1: Build ────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy solution and project files first (layer caching)
COPY IPRO.sln .
COPY src/IPRO.Entities/IPRO.Entities.csproj             src/IPRO.Entities/
COPY src/IPRO.DataAccess/IPRO.DataAccess.csproj         src/IPRO.DataAccess/
COPY src/IPRO.Business/IPRO.Business.csproj             src/IPRO.Business/
COPY src/IPRO.Utility/IPRO.Utility.csproj               src/IPRO.Utility/
COPY src/IPRO.Email/IPRO.Email.csproj                   src/IPRO.Email/
COPY src/IPRO.Billing/IPRO.Billing.csproj               src/IPRO.Billing/
COPY src/IPRO.Scheduler/IPRO.Scheduler.csproj           src/IPRO.Scheduler/
COPY src/IPRO.Web/IPRO.Web.csproj                       src/IPRO.Web/
COPY src/IPRO.Admin/IPRO.Admin.csproj                   src/IPRO.Admin/

RUN dotnet restore

# Copy all source code
COPY . .

# Build and publish
RUN dotnet publish src/IPRO.Web/IPRO.Web.csproj \
    -c Release -o /app/publish --no-restore

# ── Stage 2: Runtime ──────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Install curl for health checks
RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Non-root user for security
RUN adduser --disabled-password --gecos "" appuser && chown -R appuser /app
USER appuser

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

HEALTHCHECK --interval=30s --timeout=10s --start-period=30s --retries=3 \
  CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "IPRO.Web.dll"]
