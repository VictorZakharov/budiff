#!/usr/bin/env sh
set -eu

repo="VictorZakharov/budiff"
version="${BUDIFF_VERSION:-latest}"
install_dir="${BUDIFF_INSTALL_DIR:-$HOME/.local/bin}"
os="$(uname -s)"
arch="$(uname -m)"

case "$os:$arch" in
  Linux:x86_64) rid="linux-x64" ;;
  Darwin:arm64) rid="osx-arm64" ;;
  *) echo "Unsupported platform: $os $arch" >&2; exit 1 ;;
esac

asset="budiff-$rid.tar.gz"
if [ "$version" = "latest" ]; then
  url="https://github.com/$repo/releases/latest/download/$asset"
else
  url="https://github.com/$repo/releases/download/$version/$asset"
fi

tmp="$(mktemp -d)"
mkdir -p "$install_dir"
curl -fsSL "$url" -o "$tmp/$asset"
tar -xzf "$tmp/$asset" -C "$install_dir"
chmod +x "$install_dir/budiff"

case ":$PATH:" in
  *":$install_dir:"*) ;;
  *) echo "Add $install_dir to PATH before running budiff." ;;
esac

echo "Installed budiff to $install_dir"
