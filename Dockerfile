FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY app/App.csproj .
RUN dotnet restore
COPY app/ .
RUN dotnet publish --configuration Release --no-restore --output /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
WORKDIR /app
COPY --from=build /out .
ARG APP_VERSION=dev
ENV ASPNETCORE_HTTP_PORTS=8080 APP_VERSION=$APP_VERSION
# The image's "app" user, by number so runAsNonRoot can check it.
USER 1654
EXPOSE 8080
ENTRYPOINT ["dotnet", "App.dll"]
