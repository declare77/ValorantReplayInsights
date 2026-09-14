# Builds the hosted "upload your .vrf" backend (src/VrfInsights.Web) as one container image, for
# Cloud Run or any other host that runs a plain Linux container. See server/README.md for the
# actual deploy command and Firebase wiring.
#
# Three stages:
#   1. Build vrfkit itself from its own published source (unmodified) -- the ONLY place in this
#      whole image that touches vrfkit's code. This project's own C# never opens a .vrf file or
#      decodes anything; it only launches the binary this stage produces, as an external process.
#   2. Build the .NET backend (VrfInsights.Web), which is the same VrfInsights.Pipeline code the
#      desktop CLI/GUI already use, wrapped in a small HTTP API.
#   3. Copy both into a slim final image.

FROM rust:latest AS vrfkit-build
RUN apt-get update \
    && apt-get install -y --no-install-recommends git ca-certificates \
    && rm -rf /var/lib/apt/lists/*
# Pinned to vrfkit's own repo, unmodified -- see VrfkitBootstrapper.cs for the equivalent local
# (non-container) setup this mirrors, and vrfkit's own README for its MSRV (1.86+, satisfied by
# the rust:latest base image above) and the exact build command below.
RUN git clone --depth 1 https://github.com/yakisoba0728/vrfkit.git /vrfkit
WORKDIR /vrfkit
RUN cargo build --release -p vrfkit --features export

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS webapp-build
WORKDIR /src
COPY . .
RUN dotnet publish src/VrfInsights.Web/VrfInsights.Web.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=vrfkit-build /vrfkit/target/release/vrfkit /app/vrfkit/vrfkit
COPY --from=webapp-build /app/publish .
# The viewer webpage itself, served as static files by Program.cs (app.UseStaticFiles()) at
# /viewer/... -- so this one container is the whole site, no separate static host needed.
# NOTE: assets/ (map/agent/weapon art from scripts/Fetch-Assets.ps1) is deliberately NOT copied in
# here -- it's gitignored/fetched locally, not part of this repo checkout. The viewer already
# degrades gracefully without it (plain colored shapes/fallback icons instead of real art -- same
# as running it locally before ever running that script). Add a COPY line for assets/ here later
# if you want the real art in the hosted version too.
COPY viewer/ /app/wwwroot/viewer/
ENV VRFKIT_EXE_PATH=/app/vrfkit/vrfkit
# Cloud Run sets $PORT itself and expects the container to listen on it -- Program.cs reads this
# env var directly (defaulting to 8080 for local `docker run`/`dotnet run`), so no ASPNETCORE_URLS
# override is needed here.
EXPOSE 8080
ENTRYPOINT ["dotnet", "VrfInsights.Web.dll"]
