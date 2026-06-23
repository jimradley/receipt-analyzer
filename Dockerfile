FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy project files first so workload restore and NuGet restore are cached layers
COPY ["ReceiptAnalyzer.sln", "."]
COPY ["src/ReceiptAnalyzer.Api/ReceiptAnalyzer.Api.csproj",         "src/ReceiptAnalyzer.Api/"]
COPY ["src/ReceiptAnalyzer.Agent/ReceiptAnalyzer.Agent.csproj",     "src/ReceiptAnalyzer.Agent/"]
COPY ["src/ReceiptAnalyzer.Ledger/ReceiptAnalyzer.Ledger.csproj",   "src/ReceiptAnalyzer.Ledger/"]
COPY ["src/ReceiptAnalyzer.Reports/ReceiptAnalyzer.Reports.csproj", "src/ReceiptAnalyzer.Reports/"]
COPY ["src/ReceiptAnalyzer.Pwa/ReceiptAnalyzer.Pwa.csproj",         "src/ReceiptAnalyzer.Pwa/"]
COPY ["tests/ReceiptAnalyzer.Tests/ReceiptAnalyzer.Tests.csproj",   "tests/ReceiptAnalyzer.Tests/"]

# Restore the exact Blazor WASM runtime pack version required by the Pwa project
RUN dotnet workload restore src/ReceiptAnalyzer.Pwa/ReceiptAnalyzer.Pwa.csproj

# Restore NuGet packages
RUN dotnet restore src/ReceiptAnalyzer.Api/ReceiptAnalyzer.Api.csproj

# Copy the rest of the source and publish
COPY . .
RUN dotnet publish src/ReceiptAnalyzer.Api/ReceiptAnalyzer.Api.csproj \
    -c Release -o /app/publish --no-self-contained --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Magick.NET native runtime deps
RUN apt-get update && apt-get install -y --no-install-recommends \
    libgomp1 \
    libfontconfig1 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "ReceiptAnalyzer.Api.dll"]
