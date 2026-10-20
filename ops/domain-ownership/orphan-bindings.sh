#!/usr/bin/env bash
# orphan-bindings.sh -- 592. Prints ops/sql/orphan-bindings.sql with the web app's CURRENT hostname bindings in
# place of the list saved in it, ready to paste into the owner's read-only MySQL session (Cloud Shell, after
# SET SESSION transaction_read_only = 1). The SELECT lists the bound addresses nothing in iPro names: a
# binding left behind when a domain stopped being an adviser's (www.411trades.com, 2026-10-19).
#
# Read-only: one Azure listing, nothing changed. Removing a binding is the owner's decision.
#
#   RG   the web app's resource group   (default: ipro-production)
#   APP  the web app                    (default: ipro-prod-web)
set -euo pipefail
RG="${RG:-ipro-production}"
APP="${APP:-ipro-prod-web}"
here="$(cd "$(dirname "$0")" && pwd)"
sql="$here/../sql/orphan-bindings.sql"

names=$(az webapp config hostname list -g "$RG" --webapp-name "$APP" --query "[].name" -o tsv 2>/dev/null | tr -d '\r' | sort -u)
if [ -z "$names" ]; then
  echo "No bindings came back for $APP in $RG (signed in to the right subscription?)" >&2
  exit 1
fi

list=""
first=1
for n in $names; do
  # Host names only: anything else is not pasted into SQL.
  case "$n" in
    *[!A-Za-z0-9.*-]*) echo "skipped an unexpected binding name: $n" >&2; continue ;;
  esac
  if [ $first -eq 1 ]; then list="  SELECT '$n'"; first=0; else list="$list
  UNION ALL SELECT '$n'"; fi
done

LIST="$list" TODAY="$(date -u +%Y-%m-%d)" awk '
  /-- bound names: begin/ { print "  -- bound names: begin (" ENVIRON["TODAY"] ")"; print ENVIRON["LIST"]; skip = 1; next }
  /-- bound names: end/   { skip = 0 }
  !skip { print }
' "$sql"
