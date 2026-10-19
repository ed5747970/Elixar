#!/bin/sh
# Builds the Ubuntu / Debian / Mint package: elixar_<version>_all.deb
# usage: build-deb.sh <version> <Elixar.exe> <icon.png> <output.deb>
# Installed with "sudo apt install ./elixar_<version>_all.deb", which also installs Mono.
set -eu
VER="$1"; EXE="$2"; ICON="$3"; OUT="$4"
ROOT=$(mktemp -d)
chmod 755 "$ROOT"
trap 'rm -rf "$ROOT"' EXIT
HERE=$(cd "$(dirname "$0")" && pwd)
install -Dm644 "$EXE" "$ROOT/usr/lib/elixar/Elixar.exe"
# the system helper (root-owned, fixed actions only) and its polkit message
install -Dm755 "$HERE/linux/elixar-root" "$ROOT/usr/lib/elixar/elixar-root"
install -Dm644 "$HERE/linux/com.elisar.elixar.policy" "$ROOT/usr/share/polkit-1/actions/com.elisar.elixar.policy"
mkdir -p "$ROOT/usr/bin"
cat > "$ROOT/usr/bin/elixar" <<'SH'
#!/bin/sh
# Elixar runs as you; it asks for your password (pkexec) only for its fixed system commands.
exec /usr/bin/mono /usr/lib/elixar/Elixar.exe "$@"
SH
chmod 755 "$ROOT/usr/bin/elixar"
install -Dm644 "$ICON" "$ROOT/usr/share/icons/hicolor/128x128/apps/elixar.png"
mkdir -p "$ROOT/usr/share/applications" "$ROOT/usr/share/doc/elixar" "$ROOT/DEBIAN"
cat > "$ROOT/usr/share/applications/elixar.desktop" <<'DESK'
[Desktop Entry]
Type=Application
Name=Elixar
GenericName=Disk cleaner
Comment=The cure for a cluttered PC
Exec=elixar
Icon=elixar
Terminal=false
Categories=System;Utility;
Keywords=clean;disk;space;cache;trash;
DESK
cat > "$ROOT/usr/share/doc/elixar/copyright" <<'COPY'
Elixar - made by Elisar. All rights reserved.
COPY
SIZE=$(du -sk "$ROOT/usr" | cut -f1)
cat > "$ROOT/DEBIAN/control" <<CTRL
Package: elixar
Version: $VER
Section: utils
Priority: optional
Architecture: all
Installed-Size: $SIZE
Maintainer: Elisar <337002386+ed5747970@users.noreply.github.com>
Depends: mono-runtime (>= 6.8), libmono-system-windows-forms4.0-cil, libmono-system-drawing4.0-cil, libmono-system-core4.0-cil, libmono-system4.0-cil, libmono-corlib4.5-cil, libmono-system-io-compression4.0-cil, libmono-system-io-compression-filesystem4.0-cil, libmono-system-security4.0-cil, libmono-system-serviceprocess4.0-cil, libmono-system-web-extensions4.0-cil, libmono-system-xml4.0-cil, libgdiplus, ca-certificates-mono
Recommends: pkexec | policykit-1, libnotify-bin, xdg-utils, libglib2.0-bin, libsecret-tools
Homepage: https://github.com/ed5747970/elixar
Description: The cure for a cluttered PC
 Elixar shows what fills your disks and cleans what is safe to clean:
 caches, thumbnails, old update packages (apt), old Snap versions, old
 system logs and crash reports. It runs as you and asks for your password
 only for the system parts. Email reports, schedules, English and Hebrew,
 light and dark.
CTRL
# checksums of every file, so "dpkg --verify elixar" (and Elixar's "Verify this copy") can spot a changed file
(cd "$ROOT" && find usr -type f -print0 | sort -z | xargs -0 md5sum > DEBIAN/md5sums)
chmod 644 "$ROOT/DEBIAN/md5sums"
dpkg-deb --root-owner-group --build "$ROOT" "$OUT" >/dev/null
echo "$OUT"
