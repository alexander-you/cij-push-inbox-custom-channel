#!/usr/bin/env python3
"""Hebrew (1037) translation workflow for the AlexPushInboxChannel solution.

  export  ORG OUT.zip          ExportTranslation for the solution (Web API)
  fill    IN.zip OUT.zip       Write 1037 from he_glossary.HE for every label we own
  import  ORG IN.zip           ImportTranslation, then publish the table
  verify  ORG                  Check DisplayName/Description/options per language in metadata

ORG is https://<org>.crm.dynamics.com. Auth: `az login` (token for ORG). Requires lxml for `fill`.
"""
import base64, io, json, re, subprocess, sys, urllib.request, urllib.error, uuid, zipfile

SOLUTION = "AlexPushInboxChannel"
TABLE = "alex_pushchannelinstance"
MICROSOFT_ROWS = {"msdyn_channelinstance", "RibbonCustomization"}  # Microsoft already ships Hebrew for these
HEB = re.compile("[\u0590-\u05FF]")
SS = "urn:schemas-microsoft-com:office:spreadsheet"


def api(org, method, path, body=None, timeout=900):
    tok = subprocess.check_output(["az", "account", "get-access-token", "--resource", org,
                                   "--query", "accessToken", "-o", "tsv"]).decode().strip()
    req = urllib.request.Request(f"{org.rstrip('/')}/api/data/v9.2/{path}", method=method,
                                 data=None if body is None else json.dumps(body).encode(),
                                 headers={"Authorization": "Bearer " + tok, "Accept": "application/json",
                                          "Content-Type": "application/json; charset=utf-8"})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            t = r.read().decode()
            return json.loads(t) if t else {}
    except urllib.error.HTTPError as e:
        sys.exit(f"{method} {path} -> {e.code}: {e.read().decode()[:800]}")


def export(org, out):
    r = api(org, "POST", "solutions/Microsoft.Dynamics.CRM.ExportTranslation", {"SolutionName": SOLUTION})
    open(out, "wb").write(base64.b64decode(r["ExportTranslationFile"]))
    print("exported", out)


def fill(src_zip, out_zip):
    from lxml import etree as ET
    sys.path.insert(0, __file__.rsplit("/", 1)[0])
    from he_glossary import HE, EN_FIX
    zin = zipfile.ZipFile(src_zip)
    xml = zin.read("CrmTranslations.xml").decode("utf-8-sig")
    head, body = xml[:xml.index("<Workbook")], xml[xml.index("<Workbook"):]
    root = ET.fromstring(body.encode("utf-8"))
    ns = {"ss": SS}
    ws = next(w for w in root.findall("ss:Worksheet", ns) if w.get(f"{{{SS}}}Name") == "Localized Labels")
    rows = ws.find("ss:Table", ns).findall("ss:Row", ns)

    def values(row):
        vals = []
        for c in row.findall("ss:Cell", ns):
            if c.get(f"{{{SS}}}Index"):
                vals += [""] * (int(c.get(f"{{{SS}}}Index")) - 1 - len(vals))
            d = c.find("ss:Data", ns)
            vals.append(d.text if d is not None and d.text else "")
        return vals

    hdr = values(rows[0]); ci = {c: i for i, c in enumerate(hdr)}
    stats = dict(filled_empty=0, replaced_english=0, replaced_other=0, skipped_microsoft=0, en_fixed=0)
    missing = set()
    for row in rows[1:]:
        v = values(row) + [""] * len(hdr); v = v[:len(hdr)]
        if v[0] in MICROSOFT_ROWS:
            stats["skipped_microsoft"] += 1; continue
        en = EN_FIX.get((v[0], v[2], v[ci["1033"]]), v[ci["1033"]])
        stats["en_fixed"] += en != v[ci["1033"]]
        if en not in HE:
            missing.add(en); continue
        old = v[ci["1037"]]
        if not old: stats["filled_empty"] += 1
        elif not HEB.search(old): stats["replaced_english"] += 1
        elif old != HE[en]: stats["replaced_other"] += 1
        v[ci["1033"]], v[ci["1037"]] = en, HE[en]
        styles = [c.get(f"{{{SS}}}StyleID") for c in row.findall("ss:Cell", ns)]
        for c in list(row): row.remove(c)
        for i, val in enumerate(v):
            c = ET.SubElement(row, f"{{{SS}}}Cell")
            if i < len(styles) and styles[i]: c.set(f"{{{SS}}}StyleID", styles[i])
            if val:
                d = ET.SubElement(c, f"{{{SS}}}Data"); d.set(f"{{{SS}}}Type", "String"); d.text = val
    if missing:
        sys.exit("No Hebrew in he_glossary.HE for:\n  " + "\n  ".join(sorted(missing)))
    with zipfile.ZipFile(out_zip, "w", zipfile.ZIP_DEFLATED) as zout:
        for n in zin.namelist():
            data = (head + ET.tostring(root, encoding="unicode")).encode("utf-8") if n == "CrmTranslations.xml" else zin.read(n)
            zout.writestr(n, data)
    print(stats, "->", out_zip)


def do_import(org, zip_path):
    job = str(uuid.uuid4())
    api(org, "POST", "ImportTranslation",
        {"TranslationFile": base64.b64encode(open(zip_path, "rb").read()).decode(), "ImportJobId": job})
    j = api(org, "GET", f"importjobs({job})?$select=progress,completedon")
    print("import job", job, "progress", j.get("progress"))
    api(org, "POST", "PublishXml",
        {"ParameterXml": f"<importexportxml><entities><entity>{TABLE}</entity></entities></importexportxml>"})
    print("published", TABLE)


def verify(org):
    def lab(l, code):
        return next((x["Label"] for x in (l or {}).get("LocalizedLabels", []) if x["LanguageCode"] == code), "")
    base = f"EntityDefinitions(LogicalName='{TABLE}')"
    e = api(org, "GET", base + "?$select=DisplayName,DisplayCollectionName,Description")
    items = [("table." + k, e[k]) for k in ("DisplayName", "DisplayCollectionName", "Description")]
    for a in api(org, "GET", base + "/Attributes?$select=LogicalName,DisplayName,Description,AttributeOf")["value"]:
        if not a["AttributeOf"]:
            items += [(a["LogicalName"] + ".DisplayName", a["DisplayName"]), (a["LogicalName"] + ".Description", a["Description"])]
    for col, t in (("statecode", "StateAttributeMetadata"), ("statuscode", "StatusAttributeMetadata")):
        opts = api(org, "GET", f"{base}/Attributes(LogicalName='{col}')/Microsoft.Dynamics.CRM.{t}?$expand=OptionSet")["OptionSet"]["Options"]
        items += [(f"{col}.{o['Value']}", o["Label"]) for o in opts]
    bad = []
    for key, l in items:
        en, he = lab(l, 1033), lab(l, 1037)
        if not en or HEB.search(en): bad.append((key, 1033, en))
        if not he or not HEB.search(he): bad.append((key, 1037, he))
    print(f"labels checked: {len(items)}, problems: {len(bad)}")
    for b in bad: print("  ", b)
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    cmd, *a = sys.argv[1:] or ["-h"]
    {"export": export, "fill": fill, "import": do_import, "verify": verify}.get(cmd, lambda *_: sys.exit(__doc__))(*a)
