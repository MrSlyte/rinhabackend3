
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build

RUN apt-get update \
 && apt-get install -y --no-install-recommends clang gcc libc6-dev zlib1g-dev \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /src
COPY ["Rinha.Api/Rinha.Api.csproj", "Rinha.Api/"]
RUN dotnet restore "Rinha.Api/Rinha.Api.csproj"

COPY . .
WORKDIR /src/Rinha.Api

RUN dotnet publish -c Release -r linux-x64 -o /app/publish \
    /p:PublishAot=true \
    /p:PublishTrimmed=true \
    /p:StripSymbols=true \
    /p:InvariantGlobalization=true

FROM debian:bookworm-slim AS final
WORKDIR /app
COPY --from=build /app/publish .
RUN rm -f appsettings.Development.json
RUN rm -f /app/appsettings.Development.json
RUN rm -f /app/publish/appsettings.Development.json

ENTRYPOINT ["./Rinha.Api"]