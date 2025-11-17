#!/bin/bash

echo "Installing QuicShare..."

# Add Flathub remote if it doesn't exist
flatpak remote-add --user --if-not-exists flathub https://dl.flathub.org/repo/flathub.flatpakrepo

# Check if org.freedesktop.Platform//25.08 is installed, install if missing
if ! flatpak info org.freedesktop.Platform//25.08 &>/dev/null; then
    echo "Installing org.freedesktop.Platform//25.08..."
    flatpak install --user org.freedesktop.Platform//25.08 -y
else
    echo "org.freedesktop.Platform//25.08 is already installed."
fi

# Always download the latest QuicShare release
echo "Downloading the latest QuicShare release..."
wget -O QuicShare.flatpak https://github.com/zemendaniel/QuicShare/releases/latest/download/QuicShare.flatpak

# Install or upgrade QuicShare
echo "Installing/upgrading QuicShare..."
flatpak install --user --reinstall QuicShare.flatpak -y

# Clean up
rm QuicShare.flatpak

echo "QuicShare was installed/upgraded successfully."
