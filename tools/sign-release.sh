#!/bin/sh
# Writes SHA256SUMS for the release files and, with your own GPG key, a signature next to it.
# usage: sign-release.sh <folder with the release files> [gpg key id or email]
# People check a download with:  sha256sum -c SHA256SUMS   (and  gpg --verify SHA256SUMS.asc SHA256SUMS)
# Keep the private key on your own computer: never put it in the repository or a build server.
set -eu
DIR="$1"; KEY="${2:-}"
cd "$DIR"
find . -maxdepth 1 -type f \( -name '*.zip' -o -name '*.deb' -o -name '*.exe' \) -printf '%f\n' | sort | xargs -r sha256sum > SHA256SUMS
cat SHA256SUMS
if [ -n "$KEY" ]; then
  gpg --batch --yes --local-user "$KEY" --armor --detach-sign --output SHA256SUMS.asc SHA256SUMS
  echo "signed: SHA256SUMS.asc"
else
  echo "not signed (give your GPG key as the second argument to sign)"
fi
