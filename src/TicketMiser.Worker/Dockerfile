# The standalone worker. Optional: the web app hosts the same schedule in-process by default.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Packages.props ./
COPY src/TicketMiser.Core/TicketMiser.Core.csproj                   src/TicketMiser.Core/
COPY src/TicketMiser.Data/TicketMiser.Data.csproj                   src/TicketMiser.Data/
COPY src/TicketMiser.Reliability/TicketMiser.Reliability.csproj     src/TicketMiser.Reliability/
COPY src/TicketMiser.Observability/TicketMiser.Observability.csproj src/TicketMiser.Observability/
COPY src/TicketMiser.Ingestion/TicketMiser.Ingestion.csproj         src/TicketMiser.Ingestion/
COPY src/TicketMiser.Worker/TicketMiser.Worker.csproj               src/TicketMiser.Worker/
COPY docs/runbook.md docs/

RUN dotnet restore src/TicketMiser.Worker/TicketMiser.Worker.csproj

COPY src/ src/
RUN dotnet publish src/TicketMiser.Worker/TicketMiser.Worker.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*

USER $APP_UID

COPY --from=build /app ./

ENTRYPOINT ["dotnet", "TicketMiser.Worker.dll"]
