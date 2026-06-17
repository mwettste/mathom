# syntax=docker/dockerfile:1

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore as its own cached layer — only the web project is needed to run the app.
COPY src/Mathom.Web/Mathom.Web.csproj src/Mathom.Web/
RUN dotnet restore src/Mathom.Web/Mathom.Web.csproj

# Build & publish (migrations and wwwroot are part of the web project).
COPY src/ src/
RUN dotnet publish src/Mathom.Web/Mathom.Web.csproj -c Release -o /app/publish /p:UseAppHost=false

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
# The aspnet image listens on 8080 by default; make it explicit.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Mathom.Web.dll"]
