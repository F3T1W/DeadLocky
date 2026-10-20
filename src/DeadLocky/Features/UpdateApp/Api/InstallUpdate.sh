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

printf 'ready\n' > "$transaction/ready"
for ((attempt = 0; attempt < 600; attempt++)); do
    if ! kill -0 "$parent_pid" 2>/dev/null; then
        break
    fi
    /bin/sleep 0.1
done
if kill -0 "$parent_pid" 2>/dev/null; then
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

/usr/bin/shasum -a 256 -c "$transaction/original-files.sha256"
/usr/bin/codesign --verify --deep --strict "$target"
/usr/bin/codesign --verify --deep --strict "$transaction/Next.app"

/bin/mv "$target" "$backup"
moved=true
/bin/mv "$transaction/Next.app" "$target"
installed=true
printf 'installed\n' > "$transaction/installed"
/usr/bin/open -n "$target"

# The new app confirms only after its main window has opened. Keep the previous
# bundle if confirmation never arrives, so startup failures remain recoverable.
for ((attempt = 0; attempt < 300; attempt++)); do
    if [[ -f "$transaction/confirmed" ]]; then
        /bin/rm -f "$pending"
        trap - EXIT
        /bin/rm -rf "$transaction"
        exit 0
    fi
    /bin/sleep 0.1
done
echo "The new app did not confirm startup. Previous app retained at: $backup"
/bin/rm -f "$pending"
trap - EXIT
