FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY src/WorkstationGame.Api/WorkstationGame.Api.csproj src/WorkstationGame.Api/
RUN dotnet restore src/WorkstationGame.Api/WorkstationGame.Api.csproj
COPY . .
RUN dotnet publish src/WorkstationGame.Api/WorkstationGame.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "WorkstationGame.Api.dll"]

