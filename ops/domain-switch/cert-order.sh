#!/usr/bin/env bash
# cert-order.sh <hostname> [<hostname> ...]
#
# Places ONE accepted App Service managed-certificate order per name on the web app. Needs the
# owner's go: it changes production. The name must already be bound to the app and its DNS must
# already point at the app (a www CNAME to the app's default host, or a bare-name A record to its
# inbound address). See DOCS/DOMAIN_SWITCH_RUNBOOK.md.
#
#   RG   resource group   (default: ipro-production)
#   APP  web app          (default: ipro-prod-web)
#
# - A refusal that says "Missing one DNS record" is Azure's own DNS check still seeing the old
#   record (a DNS host that lags): retried, up to 8 times, 75 seconds apart.
# - An ACCEPTED order is never placed again: a second create while the first is open may restart it.
#   The CLI prints a Python traceback and "creation in progress" on success; that is normal.
# - An accepted order that never issues is almost always CAA: the domain lists the authorities
#   allowed to issue for it and DigiCert is not one of them (ops/domain-switch/dns-check.sh says so).
RG="${RG:-ipro-production}"
APP="${APP:-ipro-prod-web}"
quiet() { tr -d '\r' | grep -v "UserWarning\|cryptography\|Traceback\|File \"\|json\.\|JSONDecodeError\|in preview\|deserialization"; }

pending=("$@")
for attempt in 1 2 3 4 5 6 7 8; do
  next=()
  for h in "${pending[@]}"; do
    out=$(az webapp config ssl create -g "$RG" -n "$APP" --hostname "$h" -o none 2>&1 | quiet | tr '\n' ' ')
    case "$out" in
      *"Missing one DNS record"*) echo "$(date +%H:%M:%S)  $h  refused: Azure's DNS check still sees the old record; will retry"; next+=("$h");;
      *"in progress"*|"") echo "$(date +%H:%M:%S)  $h  ORDER ACCEPTED, certificate being issued";;
      *"already exists"*|*"Conflict"*) echo "$(date +%H:%M:%S)  $h  an order or certificate already exists";;
      *) echo "$(date +%H:%M:%S)  $h  create said: ${out:0:300}"; next+=("$h");;
    esac
  done
  pending=("${next[@]}")
  [ ${#pending[@]} -eq 0 ] && break
  sleep 75
done
if [ ${#pending[@]} -gt 0 ]; then echo "NOT ACCEPTED YET: ${pending[*]}"; exit 1; else echo "ALL ORDERS ACCEPTED -- now run cert-wait.sh"; fi
