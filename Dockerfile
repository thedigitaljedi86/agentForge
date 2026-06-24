# syntax=docker/dockerfile:1
#
# One Dockerfile, two services. The PROJECT/DLL build args select which API to
# publish (Hub or Runner) — see docker-compose.yml. Both run on .NET 10.
#
# NOTE: this image runs the PLATFORM services (Hub + Runner). The sandbox that
# executes jobs is a separate, rootless Podman runtime (a stub in this
# milestone), so there is no Docker-in-Docker here.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
ARG PROJECT
RUN dotnet restore "$PROJECT"
RUN dotnet publish "$PROJECT" -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .
ARG DLL
ENV APP_DLL=$DLL
ENV ASPNETCORE_URLS=http://+:8080
# Development so the Swagger UI + dashboard are available out of the box.
ENV ASPNETCORE_ENVIRONMENT=Development
EXPOSE 8080
ENTRYPOINT ["sh", "-c", "exec dotnet \"$APP_DLL\""]
