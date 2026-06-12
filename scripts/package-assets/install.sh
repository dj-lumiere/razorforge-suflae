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

# macOS: browser downloads are stamped with the com.apple.quarantine attribute,
# and Gatekeeper then blocks our un-notarized binaries (the first casualty is
# the .NET host's libhostfxr.dylib). Strip the attribute from this folder so
# the toolchain can run. No-op for files that were never quarantined.
if [ "$(uname -s)" = "Darwin" ]; then
  echo "Clearing macOS quarantine attributes (Gatekeeper) ..."
  xattr -dr com.apple.quarantine "$DIR" 2>/dev/null || true
fi

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
