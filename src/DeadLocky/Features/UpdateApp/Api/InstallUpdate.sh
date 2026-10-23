#!/bin/bash
set -euo pipefail

parent_pid="$1"
target="$2"
transaction="$3"
pending="$4"

# Only operate on the unique sibling directory prepared by this updater.
[[ "$parent_pid" =~ ^[1-9][0-9]*$ ]]
[[ "$target" == /*.app && "$target" != *$'\n'* ]]
[[ "$(/usr/bin/dirname "$transaction")" == "$(/usr/bin/dirname "$target")" ]]
[[ "$(/usr/bin/basename "$transaction")" =~ ^\.DeadLockyUpdate-[0-9a-f]{32}$ ]]
[[ -f "$transaction/Next.app/Contents/Info.plist" && -f "$pending" ]]
exec >> "$transaction/install.log" 2>&1

# Diagnostic writes are best effort and must never alter installation/rollback behavior.
write_outcome() {
    local stage="$1" code="$2" temporary
    if temporary=$(/usr/bin/mktemp "${pending%/*}/outcome.XXXXXX"); then
        if (umask 077; printf '{"Stage":"%s","Code":%s,"Transaction":"%s","Timestamp":"%s"}\n' \
            "$stage" "$code" "${transaction##*/}" "$(/bin/date -u +%Y-%m-%dT%H:%M:%SZ)" > "$temporary") \
            && /bin/mv -f "$temporary" "${pending%/*}/last-outcome.json"; then
            return 0
        fi
        /bin/rm -f "$temporary" || true
    fi
    return 0
}
write_outcome ready 0

printf 'ready\n' > "$transaction/ready"
for ((attempt = 0; attempt < 600; attempt++)); do
    if ! kill -0 "$parent_pid" 2>/dev/null; then
        break
    fi
    /bin/sleep 0.1
done
if kill -0 "$parent_pid" 2>/dev/null; then
    write_outcome launcher-did-not-quit 1
    echo 'The launcher did not quit; the original app was left in place.'
    /bin/rm -f "$pending"
    exit 1
fi

backup="$transaction/Previous.app"
moved=false
installed=false
rollback() {
    code=$?
    if [[ "$code" != 0 ]]; then
        write_outcome failed "$code"
        /bin/rm -f "$pending"
    fi
    if [[ "$code" != 0 && "$moved" == true ]]; then
        if [[ "$installed" == true ]]; then
            /bin/mv "$target" "$transaction/Failed.app"
        fi
        /bin/mv "$backup" "$target"
        /bin/rm -f "$pending"
        /usr/bin/open -n "$target" || true
    fi
    exit "$code"
}
trap rollback EXIT

write_outcome verifying 0
/usr/bin/shasum -a 256 -c "$transaction/original-files.sha256"
/usr/bin/codesign --verify --deep --strict "$target"
/usr/bin/codesign --verify --deep --strict "$transaction/Next.app"

write_outcome replacing 0
/bin/mv "$target" "$backup"
moved=true
/bin/mv "$transaction/Next.app" "$target"
installed=true
printf 'installed\n' > "$transaction/installed"
/usr/bin/open -n "$target"
write_outcome awaiting-confirmation 0

# The new app confirms only after its main window has opened. Keep the previous
# bundle if confirmation never arrives, so startup failures remain recoverable.
for ((attempt = 0; attempt < 300; attempt++)); do
    if [[ -f "$transaction/confirmed" ]]; then
        write_outcome confirmed 0
        /bin/rm -f "$pending"
        trap - EXIT
        /bin/rm -rf "$transaction"
        exit 0
    fi
    /bin/sleep 0.1
done
write_outcome confirmation-timeout 1
echo "The new app did not confirm startup. Previous app retained at: $backup"
/bin/rm -f "$pending"
trap - EXIT
