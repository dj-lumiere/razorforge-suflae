#!/usr/bin/env bash
# RazorForge packaging script (Unix host -> host-RID package).
#
# Produces: dist/razorforge-v<version>-<rid>.tar.gz (+ sha256 in checksums.txt)
# where <rid> is the host's RID: linux-x64, osx-arm64, or osx-x64.
#
# IMPORTANT: packages the HOST platform only. The native runtime (razorforge_runtime)
# is built by the host toolchain, and `dotnet publish -r <foreign-rid>` would silently
# bundle the host's native binaries into a foreign-platform archive. Build the Windows
# package on Windows with scripts/package.ps1.
#
# Prerequisites: .NET 10 SDK; clang + cmake + ninja (native runtime build); the
# vendored native sources under native/ (CI fetches them; dev checkouts have them).
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

case "$(uname -s)" in
    Linux)  RID=linux-x64 ;;
    Darwin) if [[ "$(uname -m)" == "arm64" ]]; then RID=osx-arm64; else RID=osx-x64; fi ;;
    *)
        echo "package.sh packages Linux/macOS hosts; use scripts/package.ps1 on Windows." >&2
        exit 1
        ;;
esac
VERSION="${VERSION:-$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' RazorForge.csproj | head -1)}"
NAME="razorforge-v${VERSION}-${RID}"
OUT="dist/${NAME}"

echo "=== RazorForge ${VERSION} -> ${NAME} ==="
rm -rf "$OUT" "dist/${NAME}.tar.gz"
mkdir -p dist

echo "=== dotnet publish (self-contained) ==="
dotnet publish RazorForge.csproj -c Release -r "$RID" --self-contained true -o "$OUT" \
    --verbosity minimal

echo "=== flatten native runtime artifacts (installed layout) ==="
# `dotnet publish` keeps Content items under their source-relative paths
# (native/build/bin|lib). The installed layout — and the compiler's own P/Invoke
# probing + buildandrun's prebuilt-layout detection — expect them FLAT next to
# the executable, matching the dev bin/ output.
for sub in native/build/bin native/build/lib; do
    if [ -d "$OUT/$sub" ]; then
        mv "$OUT/$sub"/* "$OUT/" 2>/dev/null || true
    fi
done
rm -rf "$OUT/native"

echo "=== prune dev-only artifacts ==="
rm -rf "$OUT/RazorForge-Wiki" "$OUT/Suflae-Wiki"
find "$OUT" -name '*.pdb' -delete
cp LICENSE README.md "$OUT/"

# Short alias next to the canonical binary. (Users preferring `forge` can alias it
# themselves — the name is deliberately not shipped to avoid colliding with
# Foundry's `forge`.)
ln -sf RazorForge "$OUT/rf"

echo "=== smoke test: version + standalone codegen from the published layout ==="
"$OUT/RazorForge" version
SMOKE_DIR=$(mktemp -d)
cat > "$SMOKE_DIR/smoke.rf" <<'EOF'
module PackageSmoke

import IO/Console

routine start()
  show("packaged razorforge works")
  return
EOF
"$OUT/RazorForge" build "$SMOKE_DIR/smoke.rf" "$SMOKE_DIR/smoke.ll"
test -s "$SMOKE_DIR/smoke.ll"
echo "smoke codegen OK"
rm -rf "$SMOKE_DIR"

echo "=== archive + checksum ==="
tar -C dist -czf "dist/${NAME}.tar.gz" "$NAME"
# macOS has no sha256sum; shasum -a 256 emits the same format.
if command -v sha256sum >/dev/null 2>&1; then
    (cd dist && sha256sum "${NAME}.tar.gz" >> checksums.txt)
else
    (cd dist && shasum -a 256 "${NAME}.tar.gz" >> checksums.txt)
fi
echo "Packaged: dist/${NAME}.tar.gz"
