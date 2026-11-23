#!/usr/bin/env python3
"""Structure and localization check for the unpacked AlexPushInboxChannel solution.

Fails (exit 1) when a build rule in docs/data-model.md or docs/localization.md is broken.
Usage: python3 tests/solution/check_solution.py [--fix] [solution-folder]
  --fix  rewrites <displayname/description default="..."> to the 1033 text. Export writes the
         exporting user's UI language there, and Dataverse shows it to users of any other language.
"""
import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

FIX = "--fix" in sys.argv
ARGS = [a for a in sys.argv[1:] if a != "--fix"]
ROOT = Path(ARGS[0] if ARGS else Path(__file__).resolve().parents[2] / "solution" / "AlexPushInboxChannel")
TABLE = "alex_pushchannelinstance"
RELATIONSHIP = f"msdyn_ChannelInstance_extendedentityid_{TABLE}"
LANGS = ("1033", "1037")
HEBREW = re.compile("[\u0590-\u05FF]")
# Microsoft-owned metadata that ships inside our solution only as a reference; its labels are not ours to translate.
FOREIGN = {Path("Entities/msdyn_ChannelInstance/Entity.xml"), Path(f"Other/Relationships/{TABLE}.xml")}
# Layout-only form labels that Dataverse never renders.
HIDDEN_FORM_LABELS = {"ColorStrip"}
GUID = re.compile(r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")

errors = []


def fail(where, msg):
    errors.append(f"{where}: {msg}")


def check_pair(where, texts):
    """texts: {languagecode: text} for one localizable item."""
    for lang in LANGS:
        text = (texts.get(lang) or "").strip()
        if not text:
            fail(where, f"missing or empty {lang} label")
        elif lang == "1033" and HEBREW.search(text):
            fail(where, f"1033 label contains Hebrew: {text!r}")
        elif lang == "1037" and not HEBREW.search(text):
            fail(where, f"1037 label has no Hebrew (English copied?): {text!r}")


def check_labels(path):
    rel = path.relative_to(ROOT)
    if rel in FOREIGN:
        return
    tree = ET.parse(path)
    for parent in tree.iter():
        groups = {}
        for child in parent:
            if child.get("languagecode") is not None and child.get("description") is not None:
                groups.setdefault(child.tag, {})[child.get("languagecode")] = child.get("description")
        for tag, texts in groups.items():
            if texts.get("1033", "") in HIDDEN_FORM_LABELS:
                continue
            # Unlabeled form spacers/tabs and empty form descriptions have no text in any language and are never rendered.
            if "FormXml" in rel.parts and not any((t or "").strip() for t in texts.values()):
                continue
            check_pair(f"{rel} <{parent.tag}>/<{tag}>", texts)
        # <description default=".."> / <displayname default=".."> (custom APIs, environment variables)
        if parent.get("default") is not None and HEBREW.search(parent.get("default")):
            fail(rel, f"<{parent.tag} default> must be the English (1033) text")


def fix_defaults():
    pattern = re.compile(r'(<(\w+) default=")[^"]*(">\s*<label description=")([^"]*)(" languagecode="1033" />)')
    for path in ROOT.rglob("*.xml"):
        text = path.read_bytes().decode("utf-8")
        new = pattern.sub(lambda m: m.group(1) + m.group(4) + m.group(3) + m.group(4) + m.group(5), text)
        if new != text:
            path.write_bytes(new.encode("utf-8"))
            print(f"fixed default labels in {path.relative_to(ROOT)}")


def check_structure():
    rel_file = ROOT / "Other" / "Relationships" / f"{TABLE}.xml"
    if not rel_file.exists():
        fail(rel_file.relative_to(ROOT), "hand-authored polymorphic relationship file is missing")
    else:
        r = ET.parse(rel_file).getroot().find("EntityRelationship")
        if r is None or r.get("Name") != RELATIONSHIP:
            fail(rel_file.name, f"EntityRelationship Name must be {RELATIONSHIP}")
        else:
            expect = {"ReferencingEntityName": "msdyn_ChannelInstance", "ReferencedEntityName": TABLE,
                      "ReferencingAttributeName": "msdyn_extendedentityId", "CascadeDelete": "RemoveLink"}
            for k, v in expect.items():
                if (r.findtext(k) or "") != v:
                    fail(rel_file.name, f"{k} must be {v}")
    if f'<EntityRelationship Name="{RELATIONSHIP}" />' not in (ROOT / "Other" / "Relationships.xml").read_text(encoding="utf-8-sig"):
        fail("Other/Relationships.xml", f"missing line for {RELATIONSHIP}")
    if not (ROOT / "Entities" / "msdyn_ChannelInstance" / "Entity.xml").exists():
        fail("Entities/msdyn_ChannelInstance", "shell entity is missing")
    sol = (ROOT / "Other" / "Solution.xml").read_text(encoding="utf-8-sig")
    if not re.search(r'<RootComponent type="1" schemaName="msdyn_channelinstance" behavior="1" />', sol):
        fail("Other/Solution.xml", "msdyn_channelinstance root component with behavior=1 is missing")


def check_customizations():
    path = ROOT / "Other" / "Customizations.xml"
    root = ET.parse(path).getroot()
    for el in root.iter():
        values = [v for k, v in el.attrib.items() if k.endswith("id")]
        if el.tag.endswith("id") and el.text:
            values.append(el.text.strip())
        for g in values:
            if GUID.fullmatch(g or "") and g != g.lower():
                fail("Customizations.xml", f"GUID in <{el.tag}> must be lowercase: {g}")
    definitions = root.findall(".//msdyn_channeldefinition")
    if len(definitions) != 1:
        fail("Customizations.xml", f"expected 1 channel definition, found {len(definitions)}")
        return
    cd = definitions[0]
    form_id = (cd.findtext("msdyn_channeldefinitionexternalformid") or "").strip()
    if not (ROOT / "Entities" / TABLE / "FormXml" / "main" / f"{{{form_id}}}.xml").exists():
        fail("Customizations.xml", f"msdyn_channeldefinitionexternalformid {form_id} has no main form file")
    if (cd.findtext("msdyn_channeldefinitionexternalentity") or "").strip() != TABLE:
        fail("Customizations.xml", f"msdyn_channeldefinitionexternalentity must be {TABLE}")
    parts = root.findall(".//msdyn_channelmessagepart")
    names = [p.findtext("msdyn_name") for p in parts]
    if len(parts) != 7 or len(set(names)) != 7:
        fail("Customizations.xml", f"expected 7 unique message parts, found {names}")
    part_ids = [p.get("msdyn_channelmessagepartid") for p in parts]
    if len(set(part_ids)) != len(part_ids):
        fail("Customizations.xml", "message part GUIDs must be unique")
    for p in parts:
        d = p.findtext("msdyn_description") or ""
        if len(d) > 100:
            fail("Customizations.xml", f"message part {p.findtext('msdyn_name')}: msdyn_description is {len(d)} chars (max 100)")
        if HEBREW.search(d) or HEBREW.search(p.findtext("msdyn_displayname") or ""):
            fail("Customizations.xml", f"message part {p.findtext('msdyn_name')}: base (English) label contains Hebrew")
    locales = {l.findtext("msdyn_localeid"): json.loads(l.findtext("msdyn_localecontent"))
               for l in root.findall(".//msdyn_channeldefinitionlocale")}
    for lang in LANGS:
        if lang not in locales:
            fail("Customizations.xml", f"msdyn_channeldefinitionlocale {lang} is missing")
    keys = ["ChannelDefinition.DisplayName", "ChannelDefinition.Description"]
    keys += [f"ChannelMessagePart.{n}.{k}" for n in names for k in ("DisplayName", "Description")]
    for k in keys:
        check_pair(f"Customizations.xml locale key {k}", {lang: locales.get(lang, {}).get(k) for lang in LANGS})
        for lang in LANGS:
            if k.endswith("Description") and len(locales.get(lang, {}).get(k) or "") > 100:
                fail("Customizations.xml", f"locale {lang} {k} is longer than 100 characters")


def main():
    if not ROOT.exists():
        sys.exit(f"Solution folder not found: {ROOT}")
    if FIX:
        fix_defaults()
    check_structure()
    check_customizations()
    files = sorted(p for p in ROOT.rglob("*.xml") if p.relative_to(ROOT) != Path("Other/Customizations.xml"))
    for p in files:
        check_labels(p)
    if errors:
        print(f"FAILED: {len(errors)} problem(s)")
        for e in errors:
            print("  - " + e)
        sys.exit(1)
    print(f"OK: structure, GUIDs and 1033/1037 labels in {len(files) + 1} files")


if __name__ == "__main__":
    main()
