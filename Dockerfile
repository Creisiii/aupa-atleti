FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/AupaAtleti.Api/AupaAtleti.Api.csproj src/AupaAtleti.Api/
RUN dotnet restore src/AupaAtleti.Api
COPY src/ src/
RUN dotnet publish src/AupaAtleti.Api -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "AupaAtleti.Api.dll"]
