FROM debian:12-slim

ENV DEBIAN_FRONTEND=noninteractive
ENV DOTNET_RUNNING_IN_CONTAINER=true
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1

# Install basic tools
RUN apt-get update && apt-get install -y wget gnupg apt-transport-https ca-certificates

# Add Microsoft package repository
RUN wget https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb -O packages-microsoft-prod.deb && \
    dpkg -i packages-microsoft-prod.deb && \
    rm packages-microsoft-prod.deb

# Install .NET SDK and dependencies
RUN apt-get update && apt-get install -y dotnet-sdk-9.0 libmsquic

# Install GUI/X11 dependencies
RUN apt-get install -y --no-install-recommends \
    libfontconfig1 \
    libfreetype6 \
    libx11-6 \
    libxcomposite1 \
    libxcursor1 \
    libxdamage1 \
    libxext6 \
    libxi6 \
    libxrandr2 \
    libxrender1 \
    libxfixes3 \
    libxinerama1 \
    libice6 \
    libsm6 \
    libglib2.0-0 \
    libgtk-3-0 \
    libpango-1.0-0 \
    libcups2 \
    libasound2 \
    libssl3 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Copy source and build
COPY . /app
RUN dotnet publish /app/QuicFileSharing.GUI/QuicFileSharing.GUI.csproj \
    -c Release -r linux-x64 \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    --self-contained true

# Make executable
RUN chmod +x /app/QuicFileSharing.GUI/bin/Release/net9.0/linux-x64/publish/QuicFileSharing.GUI

# Entry point
ENTRYPOINT ["/app/QuicFileSharing.GUI/bin/Release/net9.0/linux-x64/publish/QuicFileSharing.GUI"]
