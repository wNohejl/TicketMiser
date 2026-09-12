# Build context is the repository root, because central package management and the project
# references live above this directory.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Packages.props ./
COPY src/TicketMiser.Core/TicketMiser.Core.csproj                   src/TicketMiser.Core/
COPY src/TicketMiser.Desk/TicketMiser.Desk.csproj                   src/TicketMiser.Desk/
COPY src/TicketMiser.Data/TicketMiser.Data.csproj                   src/TicketMiser.Data/
COPY src/TicketMiser.Reliability/TicketMiser.Reliability.csproj     src/TicketMiser.Reliability/
COPY src/TicketMiser.Observability/TicketMiser.Observability.csproj src/TicketMiser.Observability/
COPY src/TicketMiser.Ingestion/TicketMiser.Ingestion.csproj         src/TicketMiser.Ingestion/
COPY src/TicketMiser.Web/TicketMiser.Web.csproj                     src/TicketMiser.Web/
COPY docs/runbook.md docs/

RUN dotnet restore src/TicketMiser.Web/TicketMiser.Web.csproj

COPY src/ src/
RUN dotnet publish src/TicketMiser.Web/TicketMiser.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Npgsql probes for GSSAPI during connection setup and logs a bare error without it.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*

RUN mkdir -p /keys /https && chown -R $APP_UID:$APP_UID /keys /https

USER $APP_UID

COPY --from=build /app ./

EXPOSE 8443

ENTRYPOINT ["dotnet", "TicketMiser.Web.dll"]
