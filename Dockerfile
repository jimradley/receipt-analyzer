# Runtime-only image. The Blazor WASM app is published on the host first — the
# in-container `dotnet/sdk` build hits WASM0005 (can't resolve the WebAssembly
# runtime pack), whereas the host SDK publishes it cleanly. Build steps:
#   dotnet publish src/ReceiptAnalyzer.Api/ReceiptAnalyzer.Api.csproj -c Release -o publish
#   docker compose build
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app

# Magick.NET native runtime deps (HEIC -> JPEG conversion)
RUN apt-get update && apt-get install -y --no-install-recommends \
    libgomp1 \
    libfontconfig1 \
    && rm -rf /var/lib/apt/lists/*

COPY publish/ ./

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "ReceiptAnalyzer.Api.dll"]
