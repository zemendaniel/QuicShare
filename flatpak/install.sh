echo "Installing QuicShare..."
flatpak remote-add --user --if-not-exists flathub https://dl.flathub.org/repo/flathub.flatpakrepo
flatpak install --user org.freedesktop.Platform//25.08 -y
wget -O QuicShare.flatpak https://github.com/zemendaniel/QuicShare/releases/latest/download/QuicShare.flatpak
flatpak install --user QuicShare.flatpak -y
rm QuicShare.flatpak
echo "QuicShare was installed successfully."
