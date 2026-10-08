FROM mcr.microsoft.com/dotnet/sdk:9.0 AS builder

WORKDIR /src

COPY . .

RUN dotnet publish BBDownT -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:9.0

# install ffmpeg
RUN apt-get update && \
    apt-get install -y --no-install-recommends ffmpeg tzdata && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY --from=builder /out .

# 登录信息、配置文件保存在 /data，下载的文件保存在 /downloads
ENV BBDOWNT_DATA_DIR=/data
VOLUME ["/data", "/downloads"]

EXPOSE 23333

ENTRYPOINT ["/app/BBDownT", "serve", "-l", "http://0.0.0.0:23333", "--server-download-root", "/downloads"]
