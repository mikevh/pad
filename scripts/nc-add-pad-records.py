#!/usr/bin/env python3
"""Add pad DNS records to zorgen.io, preserving every existing record.
Backs up current records to ~/.config/namecheap/zorgen.io.backup.json first."""
import json, os, sys, urllib.parse, urllib.request, xml.etree.ElementTree as ET
USER = "mikevh"; SLD, TLD = "zorgen", "io"
KEY = open(os.path.expanduser("~/.config/namecheap/api-key")).read().strip()
IP = urllib.request.urlopen("https://api.ipify.org", timeout=10).read().decode()
NS = {"n": "http://api.namecheap.com/xml.response"}
def call(cmd, extra):
    p = {"ApiUser": USER, "ApiKey": KEY, "UserName": USER, "ClientIp": IP, "Command": cmd, "SLD": SLD, "TLD": TLD, **extra}
    data = urllib.parse.urlencode(p).encode()
    root = ET.fromstring(urllib.request.urlopen(urllib.request.Request("https://api.namecheap.com/xml.response", data=data), timeout=30).read())
    if root.get("Status") != "OK":
        sys.exit("API error: " + " | ".join(e.text or "" for e in root.iter("{%s}Error" % NS["n"])))
    return root
def get():
    r = call("namecheap.domains.dns.getHosts", {})
    return [{k: h.get(k) for k in ("Name", "Type", "Address", "MXPref", "TTL")} for h in r.iter("{%s}host" % NS["n"])]
before = get()
json.dump(before, open(os.path.expanduser("~/.config/namecheap/zorgen.io.backup.json"), "w"), indent=2)
new = [
 {"Name": "p", "Type": "CNAME", "Address": "pad-mikevh.azurewebsites.net.", "MXPref": "10", "TTL": "1800"},
 {"Name": "asuid.p", "Type": "TXT", "Address": "CBCE2AAA26D31F16D52BEB61FF9F926C775C86AC306FA001E56116A985BFD3DE", "MXPref": "10", "TTL": "1800"},
]
existing = {(h["Name"], h["Type"], h["Address"]) for h in before}
recs = before + [n for n in new if (n["Name"], n["Type"], n["Address"]) not in existing]
params = {"EmailType": "MX"}
for i, h in enumerate(recs, 1):
    params.update({f"HostName{i}": h["Name"], f"RecordType{i}": h["Type"], f"Address{i}": h["Address"], f"MXPref{i}": h["MXPref"], f"TTL{i}": h["TTL"]})
call("namecheap.domains.dns.setHosts", params)
after = get()
print(f"before={len(before)} after={len(after)}")
key = lambda h: (h["Name"], h["Type"], " ".join(h["Address"].split()))
missing = [h for h in before if key(h) not in {key(a) for a in after}]
print("existing records changed/missing:", missing or "none")
for h in after:
    if h["Name"] in ("p", "asuid.p"): print("added:", h["Name"], h["Type"], h["Address"])
