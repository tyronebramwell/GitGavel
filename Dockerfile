# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/GitGavel/GitGavel.csproj \
    -c Release \
    -r linux-musl-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -o /app/publish

# Runtime stage
FROM alpine:3.20
RUN apk add --no-cache \
    git \
    ca-certificates \
    libgcc \
    libstdc++ \
    icu-libs \
    libssl3 \
    zlib
WORKDIR /app
COPY --from=build /app/publish/GitGavel /usr/local/bin/gitgavel
RUN chmod +x /usr/local/bin/gitgavel && \
    git config --global --add safe.directory '*'

ENTRYPOINT ["gitgavel"]
