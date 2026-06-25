# syntax=docker/dockerfile:1

# ---- css (Tailwind + daisyUI; build-time only) ----
FROM node:22-alpine AS css
WORKDIR /css
COPY package.json package-lock.json ./
RUN npm ci
COPY src/ src/
RUN npx tailwindcss -i src/Mathom.Web/Styles/app.css -o src/Mathom.Web/wwwroot/css/app.css --minify

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore as its own cached layer — only the web project is needed to run the app.
COPY src/Mathom.Web/Mathom.Web.csproj src/Mathom.Web/
RUN dotnet restore src/Mathom.Web/Mathom.Web.csproj

# Build & publish (migrations and wwwroot are part of the web project).
COPY src/ src/
# Built CSS from the css stage (overlays into wwwroot before publish)
COPY --from=css /css/src/Mathom.Web/wwwroot/css/app.css src/Mathom.Web/wwwroot/css/app.css
RUN dotnet publish src/Mathom.Web/Mathom.Web.csproj -c Release -o /app/publish /p:UseAppHost=false

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
# The aspnet image listens on 8080 by default; make it explicit.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
# Run as the image's built-in non-root user (uid 1654 "app"). The media + Data
# Protection key mount points are owned by it so the app can write them as non-root.
# A fresh named volume inherits this ownership on first mount; EXISTING volumes keep
# their current (root) ownership and must be chowned to 1654 once — see docs/DEPLOYMENT.md.
RUN mkdir -p /app/media /keys && chown -R 1654:1654 /app/media /keys
USER 1654
ENTRYPOINT ["dotnet", "Mathom.Web.dll"]
