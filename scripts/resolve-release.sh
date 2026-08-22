#!/usr/bin/env bash
# Resolve the packaging version and GitHub-release branding from the pushed tag,
# writing PKG_VERSION / RELEASE_NAME / RELEASE_NOTES to $GITHUB_ENV for later steps.
#
# Two tag families feed the same release workflow (see .github/workflows/release.yaml):
#   v*      -> RazorForge release. Artifacts named for the tag version.
#   sf-v*   -> Suflae release. The SAME compiler artifacts (one binary runs both
#              languages), named for the csproj compiler version — NOT the sf tag,
#              whose "0.1.0" would collide with the existing v0.1.0 release — and
#              published with the Suflae notes and title.
set -euo pipefail

csproj_version() {
  sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' RazorForge.csproj | head -1
}

tag="${GITHUB_REF_NAME:?GITHUB_REF_NAME is not set}"

if [[ "$tag" == sf-v* ]]; then
  pkg_version="$(csproj_version)"                 # e.g. 0.4.0 — the compiler that runs .sf
  release_name="Suflae v${tag#sf-v} (preview)"    # e.g. Suflae v0.1.0 (preview)
  release_notes="scripts/package-assets/RELEASE_NOTES_SF.md"
else
  pkg_version="${tag#v}"                          # e.g. 0.4.0
  release_name="RazorForge ${tag}"
  release_notes="scripts/package-assets/RELEASE_NOTES.md"
fi

{
  echo "PKG_VERSION=${pkg_version}"
  echo "RELEASE_NAME=${release_name}"
  echo "RELEASE_NOTES=${release_notes}"
} >> "${GITHUB_ENV:?GITHUB_ENV is not set}"

echo "Resolved: tag=${tag} PKG_VERSION=${pkg_version} RELEASE_NAME='${release_name}' RELEASE_NOTES=${release_notes}"
