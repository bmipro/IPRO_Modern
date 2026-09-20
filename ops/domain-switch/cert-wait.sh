#!/usr/bin/env bash
# cert-wait.sh <minutes> <hostname> [<hostname> ...]
#
# POLLS ONLY (never places an order): waits for each name's managed certificate to carry a
# thumbprint, binds it to the name (SNI), and reports. A thumbprint is a public fingerprint, not a
# secret. With CAA in order a certificate took 2 to 13 minutes on 2026-09-20; the front ends serve a
# new binding about a minute after it is made. Needs the owner's go (it changes production).
# See DOCS/DOMAIN_SWITCH_RUNBOOK.md.
#
#   RG   resource group   (default: ipro-production)
#   APP  web app          (default: ipro-prod-web)
RG="${RG:-ipro-production}"
APP="${APP:-ipro-prod-web}"
MIN="$1"; shift
END=$(( $(date +%s) + MIN * 60 ))
quiet() { tr -d '\r' | grep -v "UserWarning\|cryptography\|in preview"; }
thumb_of() { az webapp config ssl show -g "$RG" --certificate-name "$1" --query thumbprint -o tsv 2>/dev/null | tr -d '\r'; }
bound_thumb() { az webapp show -g "$RG" -n "$APP" --query "hostNameSslStates[?name=='$1'].thumbprint | [0]" -o tsv 2>/dev/null | tr -d '\r'; }

pending=("$@")
while [ ${#pending[@]} -gt 0 ] && [ "$(date +%s)" -lt "$END" ]; do
  next=()
  for h in "${pending[@]}"; do
    t=$(thumb_of "$h")
    if [ -z "$t" ] || [ "$t" = "None" ]; then next+=("$h"); continue; fi
    echo "$(date +%H:%M:%S)  $h  ISSUED, thumbprint $t -- binding"
    az webapp config ssl bind -g "$RG" -n "$APP" --certificate-thumbprint "$t" --ssl-type SNI --hostname "$h" -o none 2>&1 | quiet | head -3
    b=$(bound_thumb "$h")
    if [ -n "$b" ] && [ "$b" != "None" ]; then echo "$(date +%H:%M:%S)  $h  BOUND"; else echo "$(date +%H:%M:%S)  $h  binding not visible yet"; next+=("$h"); fi
  done
  pending=("${next[@]}")
  if [ ${#pending[@]} -gt 0 ]; then echo "$(date +%H:%M:%S)  waiting on: ${pending[*]}"; sleep 60; fi
done
if [ ${#pending[@]} -gt 0 ]; then echo "STILL PENDING after $MIN min: ${pending[*]}  (check CAA: dns-check.sh)"; exit 1; else echo "ALL BOUND"; fi
