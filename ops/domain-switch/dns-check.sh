#!/usr/bin/env bash
# dns-check.sh <domain> [<domain> ...]
#
# Reads what a domain switch depends on from the zone's OWN nameservers, several times, because a
# DNS host can hand out old and new answers side by side for a while after a save (the legacy host
# did, for up to 45 minutes, on 2026-09-20). Read-only. See DOCS/DOMAIN_SWITCH_RUNBOOK.md.
#
#   APP_ID    the App Service custom-domain verification id   (default: ipro-prod-web's)
#   APP_IP    the app's inbound address for bare names         (default: 40.89.19.0)
#   APP_HOST  the app's default host name for www CNAMEs       (default: ipro-prod-web.azurewebsites.net)
#   READS     how many times to ask each nameserver            (default: 3)
#
# Two things this script is careful about, both learned the hard way:
#   - a plain lookup HIDES a CNAME (it prints the final address), so record types are asked for
#     by type (-type=CNAME), never inferred from an address;
#   - it never asks a PUBLIC resolver about a name that may not exist yet: a resolver remembers
#     "does not exist" for the zone's negative TTL (3 hours on the legacy host).
APP_ID="${APP_ID:-C6D5FC2CBD50987D0FF7FE648BC710D4B2454FA211AE9094BB2816A6505E8572}"
APP_IP="${APP_IP:-40.89.19.0}"
APP_HOST="${APP_HOST:-ipro-prod-web.azurewebsites.net}"
READS="${READS:-3}"

q() { nslookup "$@" 2>/dev/null | tr -d '\r'; }
txt() { q -type=TXT "$1" "$2" | awk '/text =/ {f=1; next} f && /"/ {gsub(/^[ \t]*"|"[ \t]*$/, ""); print; exit}'; }
idcheck() { if [ -z "$1" ]; then echo "missing"; elif [ "$1" = "$APP_ID" ]; then echo "correct"; else echo "WRONG VALUE (${#1} chars; the Record box takes the id, the Name box takes asuid)"; fi; }

for d in "$@"; do
  echo "================ $d"
  servers=$(q -type=NS "$d" 8.8.8.8 | awk '/nameserver =/ {print $NF}' | sed 's/\.$//' | sort -u)
  [ -z "$servers" ] && { echo "  no nameservers found for $d"; continue; }
  for ns in $servers; do
    for i in $(seq 1 "$READS"); do
      serial=$(q -type=SOA "$d" "$ns" | awk '/serial/ {print $NF}')
      mailc=$(q -type=CNAME "mail.$d" "$ns" | awk '/canonical name/ {print $NF}')
      maila=$(q -type=A "mail.$d" "$ns" | awk '/^Address/ && !/#53/ {x=$2} END {print x}')
      mx=$(q -type=MX "$d" "$ns" | awk '/mail exchanger/ {print $NF}' | tr '\n' ' ')
      apex=$(q -type=A "$d" "$ns" | awk '/^Address/ && !/#53/ {x=$2} END {print x}')
      wwwc=$(q -type=CNAME "www.$d" "$ns" | awk '/canonical name/ {print $NF}')
      if [ -n "$mailc" ]; then mailtxt="CNAME -> $mailc"; [ "$mailc" = "$d" ] && mailtxt="$mailtxt  ** follows the bare name: pin it to an A record BEFORE the bare name moves **"; else mailtxt="A $maila"; fi
      case " $mx " in *" $d "*) mxtxt="$mx ** points at the bare name: change it to mail.$d BEFORE the bare name moves **";; *) mxtxt="$mx";; esac
      case "$apex" in "$APP_IP") apextxt="$apex (the platform)";; *) apextxt="$apex";; esac
      case "$wwwc" in "$APP_HOST") wwwtxt="CNAME -> $wwwc (the platform)";; "") wwwtxt="(no CNAME)";; *) wwwtxt="CNAME -> $wwwc";; esac
      echo "  [$ns read $i] serial $serial"
      echo "        bare name   $apextxt"
      echo "        www         $wwwtxt"
      echo "        mail        $mailtxt"
      echo "        MX          $mxtxt"
      echo "        asuid       $(idcheck "$(txt "asuid.$d" "$ns")")"
      echo "        asuid.www   $(idcheck "$(txt "asuid.www.$d" "$ns")")"
    done
  done
  # The Question part of the reply names type 257 too, so only what follows "Answer" counts.
  caa=$(curl -s -m 15 "https://dns.google/resolve?name=$d&type=CAA" | tr -d '\r')
  answers="${caa#*\"Answer\"}"
  if [ "$answers" != "$caa" ] && echo "$answers" | grep -q '"type":257'; then
    if echo "$answers" | grep -qi "digicert.com"; then echo "  CAA: present, DigiCert allowed"; else echo "  CAA: present and DigiCert is NOT listed ** add  0 issue \"digicert.com\"  or no managed certificate will ever issue **"; fi
  else
    echo "  CAA: none (any authority may issue)"
  fi
done
