FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
USER root
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

RUN apt-get update && \
    apt-get -qy full-upgrade && \
    apt-get install -qy curl bash ca-certificates gnupg && \
    curl -sSL https://get.docker.com/ | sh

WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["ModelScanner.csproj", "."]
RUN dotnet restore "./ModelScanner.csproj"
COPY . .
WORKDIR "/src/."
RUN dotnet build "ModelScanner.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "ModelScanner.csproj" -c Release -o /app/publish /p:UseAppHost=false


FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "ModelScanner.dll"]