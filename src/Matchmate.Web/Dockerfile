FROM mcr.microsoft.com/dotnet/sdk:11.0.100-rc.2-resolute AS build
WORKDIR /source
COPY global.json Directory.Build.props ./
COPY src/Matchmate.Contracts/ src/Matchmate.Contracts/
COPY src/Matchmate.Core/ src/Matchmate.Core/
COPY src/Matchmate.ServiceDefaults/ src/Matchmate.ServiceDefaults/
COPY src/Matchmate.Web/ src/Matchmate.Web/
RUN dotnet publish src/Matchmate.Web/Matchmate.Web.csproj -c Release -o /out -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-rc.2-resolute AS runtime
WORKDIR /app
COPY --from=build /out ./
ENV ASPNETCORE_HTTP_PORTS=8080
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Matchmate.Web.dll"]
