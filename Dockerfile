FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY devocional.csproj ./
RUN dotnet restore
COPY . ./
RUN dotnet publish devocional.csproj --configuration Release --output /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /app/publish ./
USER $APP_UID
ENTRYPOINT ["dotnet", "devocional.dll"]
