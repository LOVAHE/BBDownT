FROM mcr.microsoft.com/dotnet/sdk:9.0 AS builder

WORKDIR /src

COPY . .

RUN dotnet build BBDownT/BBDownT.csproj -c Release

FROM mcr.microsoft.com/dotnet/aspnet:9.0

RUN apt-get update && \
    apt-get install -y --no-install-recommends ffmpeg && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY --from=builder /src/BBDownT/bin/Release/net9.0 .

EXPOSE 23333

ENTRYPOINT ["/app/BBDownT", "serve", "-l", "http://0.0.0.0:23333"]
