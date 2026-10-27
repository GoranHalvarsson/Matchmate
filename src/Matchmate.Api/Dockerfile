FROM mcr.microsoft.com/dotnet/sdk:11.0.100-rc.2-resolute AS build
WORKDIR /source
COPY global.json Directory.Build.props ./
COPY src/Matchmate.Contracts/ src/Matchmate.Contracts/
COPY src/Matchmate.Core/ src/Matchmate.Core/
COPY src/Matchmate.Infrastructure/ src/Matchmate.Infrastructure/
COPY src/Matchmate.ServiceDefaults/ src/Matchmate.ServiceDefaults/
COPY src/Matchmate.Api/ src/Matchmate.Api/
COPY data/match-enrichment/brentford-chelsea-2026-09-18-player-briefs-v2.json data/match-enrichment/
COPY data/generated-replay-distance/brentford-full-v5-distance-v2.gz data/generated-replay-distance/
COPY data/generated-replay-distance/liverpool-actions-v1-distance-v1.gz data/generated-replay-distance/
RUN dotnet publish src/Matchmate.Api/Matchmate.Api.csproj -c Release -o /out -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-rc.2-resolute AS runtime
WORKDIR /app
COPY --from=build /out ./
ENV ASPNETCORE_HTTP_PORTS=8080
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Matchmate.Api.dll"]
