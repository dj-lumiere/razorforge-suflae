#!/usr/bin/env bash
# RazorForge installer (Linux / macOS)
#
# Symlinks `razorforge` and `rf` into ~/.local/bin so they work from any
# terminal. Everything runs from this folder — no root, no other downloads.
#
# Usage:  ./install.sh
set -euo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BIN="${HOME}/.local/bin"
mkdir -p "$BIN"
ln -sf "$DIR/RazorForge" "$BIN/razorforge"
ln -sf "$DIR/RazorForge" "$BIN/rf"
echo "Linked razorforge + rf into $BIN"

case ":$PATH:" in
  *":$BIN:"*)
    echo "Ready — try: razorforge version"
    ;;
  *)
    echo "NOTE: $BIN is not on your PATH yet. Add it, e.g.:"
    # shellcheck disable=SC2016
    echo '  echo '\''export PATH="$HOME/.local/bin:$PATH"'\'' >> ~/.profile && . ~/.profile'
    ;;
esac
echo
echo "See QUICKSTART.md for a hello-world walkthrough."
