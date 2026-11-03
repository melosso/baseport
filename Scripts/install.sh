#!/usr/bin/env bash
set -euo pipefail

REPO="${BASEPORT_REPO:-melosso/baseport}"
# Root installs exist to be services, they default where a service account can read: /root is 0700, /opt is not.
if [ "$(id -u)" = "0" ]; then
  DIR="${BASEPORT_DIR:-/opt/baseport}"
  BIN="${BASEPORT_BIN:-/usr/local/bin}"
else
  DIR="${BASEPORT_DIR:-$HOME/.baseport}"
  BIN="${BASEPORT_BIN:-$HOME/.local/bin}"
fi
API="https://api.github.com/repos/$REPO/releases"
INSTALLER="https://raw.githubusercontent.com/$REPO/main/Scripts/install.sh"

fail() { echo "$*" >&2; exit 1; }

[ "$(uname -s)" = "Linux" ] || fail "This installer covers Linux. On Windows use install.ps1."

case "$(uname -m)" in
  x86_64|amd64) ARCH="x64" ;;
  *) fail "There is no Baseport build for $(uname -m). Releases ship linux-x64 and win-x64." ;;
esac

for CMD in curl tar sha256sum; do
  command -v "$CMD" >/dev/null 2>&1 || fail "This installer needs $CMD, which is not on this machine."
done

# The install fails halfway through without this, once the download is already done and the user is already waiting.
writable() {
  local P="$1"
  while [ ! -e "$P" ]; do P=$(dirname "$P"); done
  [ -w "$P" ]
}
writable "$DIR" || fail "You cannot write to $DIR. Install as root, or somewhere you own:
  curl -sSL $INSTALLER | sudo bash
  BASEPORT_DIR=\"\$HOME/.baseport\" BASEPORT_BIN=\"\$HOME/.local/bin\" curl -sSL $INSTALLER | bash"
writable "$BIN" || fail "You cannot write to $BIN. Install as root, or pick another directory with BASEPORT_BIN:
  curl -sSL $INSTALLER | sudo bash
  BASEPORT_BIN=\"\$HOME/.local/bin\" curl -sSL $INSTALLER | bash"

TAG="${BASEPORT_VERSION:-}"
if [ -z "$TAG" ]; then
  TAG=$(curl -fsSL "$API/latest" 2>/dev/null | sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -1)
fi
if [ -z "$TAG" ]; then
  TAG=$(curl -fsSL "$API?per_page=1" 2>/dev/null | sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -1)
fi
[ -n "$TAG" ] || fail "Could not resolve a release tag from $REPO."

ASSET="Baseport-$TAG-linux-$ARCH.tar.gz"
BASE="https://github.com/$REPO/releases/download/$TAG"

TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

echo "Installing $TAG into $DIR (override with BASEPORT_DIR)"
curl -fSL --progress-bar -o "$TMP/$ASSET" "$BASE/$ASSET" || fail "Release $TAG has no asset named $ASSET."
curl -fsSL -o "$TMP/$ASSET.sha256" "$BASE/$ASSET.sha256" || fail "Release $TAG has no checksum for $ASSET."

( cd "$TMP" && sha256sum -c "$ASSET.sha256" >/dev/null 2>&1 ) \
  || fail "Checksum mismatch for $ASSET. Refusing to install."

mkdir -p "$TMP/payload"
tar -xzf "$TMP/$ASSET" -C "$TMP/payload"

for KEEP in baseport.db baseport.db-shm baseport.db-wal baseport.key .env log uploads backups; do
  rm -rf "${TMP:?}/payload/$KEEP"
done

UPDATE="no"
if [ -e "$DIR/Baseport" ]; then UPDATE="yes"; fi
if [ -f "$DIR/appsettings.json" ]; then rm -f "$TMP/payload/appsettings.json"; fi

mkdir -p "$DIR"
cp -R "$TMP/payload/." "$DIR/"
chmod +x "$DIR/Baseport"

if [ ! -e "$DIR/.env" ]; then
  cat > "$DIR/.env" <<'ENVFILE'
# Precedence: appsettings.json < this file < process environment variables < command-line arguments.
# BASEPORT_URLS=http://localhost:5000
# BASEPORT_ADMIN_ADDRESS=127.0.0.1:5264
# BASEPORT_TRUST_FORWARDED_HEADERS=false
# BASEPORT_CONNECTION_STRING=Data Source=baseport.db
# BASEPORT_ALLOW_INSECURE_SIGNIN=false
# BASEPORT_ALLOW_REMOTE_PROVIDERS=false
# BASEPORT_MAX_CONNECTIONS=1000
ENVFILE
  chmod 600 "$DIR/.env"
  if [ "$(id -u)" = "0" ] && id baseport >/dev/null 2>&1 && [ "$(stat -c %U "$DIR")" = "baseport" ]; then
    chown baseport:baseport "$DIR/.env"
  fi
fi

mkdir -p "$BIN"
cat > "$BIN/baseport.new" <<EOF
#!/bin/sh
# baseport wrapper
DIR="$DIR"
BIN="$BIN"
REPO="$REPO"
INSTALLER="$INSTALLER"
EOF
cat >> "$BIN/baseport.new" <<'SHIM'
set -eu
BASEPORT_COMMAND="$BIN/baseport"
BASEPORT_CALLER_DIR=$PWD
export BASEPORT_COMMAND BASEPORT_CALLER_DIR

# A running binary cannot replace itself, so update is the one command that stays here.
if [ "${1:-}" = "update" ]; then
  if { [ ! -w "$DIR" ] || [ ! -w "$BIN" ]; } && [ "$(id -u)" != "0" ]; then exec sudo -- "$0" "$@"; fi
  [ -x "$DIR/Baseport" ] && "$DIR/Baseport" stop --force
  TMP=$(mktemp)
  trap 'rm -f "$TMP"' EXIT INT TERM
  curl -fsSL "$INSTALLER" -o "$TMP" || { echo "Could not download the installer from $INSTALLER." >&2; exit 1; }
  [ -s "$TMP" ] || { echo "The installer downloaded from $INSTALLER was empty." >&2; exit 1; }
  BASEPORT_REPO="$REPO" BASEPORT_DIR="$DIR" BASEPORT_BIN="$BIN" bash "$TMP"
  exit
fi

[ -x "$DIR/Baseport" ] || { echo "No Baseport in $DIR. Reinstall it: curl -sSL $INSTALLER | bash" >&2; exit 1; }
cd "$DIR"
exec "$DIR/Baseport" "$@"
SHIM
chmod +x "$BIN/baseport.new"
mv -f "$BIN/baseport.new" "$BIN/baseport"

# A wrapper from an earlier install keeps its own directory and can shadow this one on PATH, drop the ones whose directory is gone.
for OTHER in /usr/local/bin/baseport "$HOME/.local/bin/baseport"; do
  if [ "$OTHER" = "$BIN/baseport" ] || [ ! -f "$OTHER" ]; then continue; fi
  if [ "$(head -1 "$OTHER")" != "#!/bin/sh" ]; then continue; fi
  # This wrapper keeps its directory in a DIR= line; the ones before it inlined the path into every exec.
  OTHERDIR=$(sed -n 's/^DIR="\(.*\)"$/\1/p' "$OTHER" | head -1)
  if [ -z "$OTHERDIR" ]; then
    OTHERDIR=$(grep -o '"[^"]*/Baseport"' "$OTHER" | head -1 | tr -d '"' || true)
    OTHERDIR=${OTHERDIR%/Baseport}
  fi
  if [ -z "$OTHERDIR" ] || [ -x "$OTHERDIR/Baseport" ]; then continue; fi
  rm -f "$OTHER"
  echo "Removed the wrapper at $OTHER, which pointed at the removed $OTHERDIR."
done

echo
if [ "$UPDATE" = "yes" ]; then
  echo "Baseport updated to $TAG in $DIR."
else
  echo "Baseport $TAG installed in $DIR."
fi

# A home directory is 0700 on most distros, a service account could not read the install there.
case "$DIR" in
  /root|/root/*|/home/*) SERVICE_OK=no ;;
  *) SERVICE_OK=yes ;;
esac

# An update leaves the service on the old binary until it restarts, do it here instead of telling the user to.
if [ "$(id -u)" = "0" ] && [ -e /etc/systemd/system/baseport.service ]; then
  RUNDIR=$(systemctl show baseport.service -p WorkingDirectory --value 2>/dev/null || true)
  if [ "$RUNDIR" = "$DIR" ]; then
    systemctl restart baseport && echo "baseport.service restarted on the new binary."
  else
    echo "Note: baseport.service runs from $RUNDIR, which was not updated."
    echo "  BASEPORT_DIR=\"$RUNDIR\" curl -sSL $INSTALLER | sudo bash"
  fi
fi

echo
echo "  baseport                             start on http://localhost:5000"
echo "  baseport --urls http://0.0.0.0:5000  start on every interface"
if [ "$SERVICE_OK" = "yes" ]; then
  echo "  sudo baseport service [--urls URL]   run it as a systemd service"
  echo "  sudo baseport start|stop|restart     control that service"
fi
echo "  baseport stop --force                stop it however it is running"
echo "  baseport status                      is it running, and where"
echo "  baseport logs                        follow the logs, and show the first-start login"
echo "  baseport doctor                      check this install"
echo "  baseport update                      pull the latest release"
echo "  baseport uninstall [--purge]         remove it, --purge deletes the data too"
echo "  baseport help                        everything else"
echo
echo "Console http://localhost:5000/_/admin, first start prints a one-time admin login."

case ":$PATH:" in
  *":$BIN:"*) ;;
  *)
    echo
    echo "$BIN is not on your PATH. Add it:"
    echo "  echo 'export PATH=\"$BIN:\$PATH\"' >> ~/.bashrc && . ~/.bashrc"
    ;;
esac
