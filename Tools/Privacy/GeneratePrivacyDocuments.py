#!/usr/bin/env python3
"""Generate offline Markdown and static HTML from the app's EN/HU policy resources.

Run from any directory. --check reports stale outputs without writing files.
No network access, third-party packages or .NET commands are used.
"""
from __future__ import annotations

import argparse
from datetime import date
from html import escape
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ASSETS = ROOT / "Common/Frontend/Assets/Privacy"
OUTPUT = ROOT / "Docs/Privacy"
LABELS = {
    "en_us": {
        "code": "en", "name": "English", "other": "Magyar", "other_code": "hu",
        "reviewed": "Last reviewed", "effective": "Effective from", "version": "Version",
        "status": "Notice status", "deletion": "Account and data deletion — Password Manager Local",
        "policy": "Full Privacy Policy", "delete_link": "Account and data deletion",
        "draft": "Draft for testing. This page describes local controls; it cannot receive deletion requests or remotely erase your devices. The public release contact and any required request route still need to be finalized.",
    },
    "hu": {
        "code": "hu", "name": "Magyar", "other": "English", "other_code": "en",
        "reviewed": "Utolsó felülvizsgálat", "effective": "Hatályos ettől", "version": "Változat",
        "status": "A tájékoztató állapota", "deletion": "Fiók és adatok törlése — Password Manager Local",
        "policy": "Teljes adatvédelmi tájékoztató", "delete_link": "Fiók és adatok törlése",
        "draft": "Tesztelési tervezet. Ez az oldal a helyi lehetőségeket ismerteti; nem fogad törlési kérelmet és nem törli távolról az eszközeidet. A nyilvános kiadás kapcsolattartóját és az esetleg szükséges kérelmezési lehetőséget még véglegesíteni kell.",
    },
}
CSS = """
:root { color-scheme: light dark; font: 1.1rem/1.65 system-ui, sans-serif; }
body { margin: 0; background: #f6f7f9; color: #182335; }
main { max-width: 72ch; margin: auto; padding: 1.5rem; overflow-wrap: anywhere; }
nav { display: flex; flex-wrap: wrap; gap: 1rem; margin: 0 0 1.5rem; }
a { color: #174d9b; text-decoration: underline; text-underline-offset: .15em; }
a:focus-visible { outline: 3px solid currentColor; outline-offset: 4px; }
h1 { font-size: 1.9rem; line-height: 1.25; } h2 { font-size: 1.25rem; line-height: 1.4; }
section { margin-top: 2rem; } .metadata { font-size: .95rem; }
aside { border-inline-start: .3rem solid #386fae; padding: .8rem 1rem; background: #e8eff9; }
@media (prefers-color-scheme: dark) {
  body { background: #121923; color: #eef2f8; } a { color: #9bcaff; }
  aside { background: #202e42; border-color: #9bcaff; }
}
@media print { body { background: white; color: black; } nav { display: none; } }
"""


def load_documents() -> dict:
    documents = {}
    for language in LABELS:
        document = json.loads((ASSETS / f"{language}.json").read_text(encoding="utf-8"))
        date.fromisoformat(document["reviewedOn"])
        if document["effectiveOn"] is not None:
            date.fromisoformat(document["effectiveOn"])
        if not document["isDraft"] and not document["effectiveOn"]:
            raise ValueError("A final notice needs an effective date")
        ids = [section["id"] for section in document["sections"]]
        if not ids or len(ids) != len(set(ids)):
            raise ValueError("Policy section IDs must be unique")
        for section in document["sections"]:
            if any(not section[field].strip() for field in ("id", "heading", "body")):
                raise ValueError("Empty policy section")
        documents[language] = document
    en, hu = documents.values()
    for field in ("version", "reviewedOn", "effectiveOn", "isDraft"):
        if en[field] != hu[field]:
            raise ValueError(f"Policy translations differ: {field}")
    if [s["id"] for s in en["sections"]] != [s["id"] for s in hu["sections"]]:
        raise ValueError("Policy translations have different section IDs")
    return documents


def paragraphs(text: str) -> str:
    # This one public authority URL is the only URL in the canonical notice.
    return "\n".join("<p>" + escape(part).replace(
        "https://www.naih.hu/", '<a href="https://www.naih.hu/">https://www.naih.hu/</a>')
        + "</p>" for part in text.split("\n\n"))


def metadata(document: dict, labels: dict) -> str:
    value = document["effectiveOn"] or document["reviewedOn"]
    label = labels["effective"] if document["effectiveOn"] else labels["reviewed"]
    return f'{labels["version"]} {document["version"]} · {label}: {value}'


def page(document: dict, labels: dict, *, deletion: bool = False) -> str:
    code = labels["code"]
    prefix = "account-deletion" if deletion else "privacy-policy"
    title = labels["deletion"] if deletion else document["title"]
    sections = document["sections"]
    if deletion:
        sections = [s for s in sections if s["id"] in {"retention-deletion", "backups", "rights"}]
    body = "\n".join(f'<section id="{escape(s["id"], quote=True)}"><h2>{escape(s["heading"])}</h2>\n'
        + paragraphs(s["body"]) + "</section>" for s in sections)
    notice = labels["draft"] if deletion and document["isDraft"] else document["statusText"]
    return f'''<!doctype html>
<html lang="{code}">
<head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>{escape(title)}</title><style>{CSS}</style></head>
<body><main>
<nav aria-label="Language / Nyelv">
<a href="{prefix}.{labels['other_code']}.html" lang="{labels['other_code']}">{labels['other']}</a>
<a href="privacy-policy.{code}.html">{labels['policy']}</a>
<a href="account-deletion.{code}.html">{labels['delete_link']}</a>
</nav>
<h1>{escape(title)}</h1>
<p class="metadata">{escape(metadata(document, labels))}</p>
{paragraphs(document['summary']) if not deletion else ''}
<aside aria-label="{escape(labels['status'])}">{paragraphs(notice)}</aside>
{body}
</main></body></html>
'''


def outputs() -> dict[Path, str]:
    result = {}
    for language, document in load_documents().items():
        labels = LABELS[language]
        code = labels["code"]
        result[OUTPUT / f"PrivacyPolicy.{code}.md"] = (
            f'# {document["title"]}\n\n{metadata(document, labels)}\n\n'
            f'> {document["statusText"]}\n\n{document["summary"]}\n\n'
            + "\n\n".join(f'## {s["heading"]}\n\n{s["body"]}' for s in document["sections"]) + "\n")
        result[OUTPUT / f"Public/privacy-policy.{code}.html"] = page(document, labels)
        result[OUTPUT / f"Public/account-deletion.{code}.html"] = page(document, labels, deletion=True)
    result[OUTPUT / "Public/index.html"] = f'''<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Password Manager Local — Privacy / Adatvédelem</title><style>{CSS}</style></head>
<body><main><h1>Password Manager Local</h1><p>Privacy / Adatvédelem</p>
<nav aria-label="Language / Nyelv"><a href="privacy-policy.en.html" lang="en">Privacy Policy — English</a>
<a href="privacy-policy.hu.html" lang="hu">Adatvédelmi tájékoztató — Magyar</a></nav>
</main></body></html>
'''
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="Fail if generated documents are missing or stale")
    args = parser.parse_args()
    stale = []
    generated = outputs()
    for path, content in generated.items():
        if args.check:
            if not path.exists() or path.read_text(encoding="utf-8") != content:
                stale.append(str(path.relative_to(ROOT)))
        else:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(content, encoding="utf-8")
    if stale:
        print("Privacy documents need regeneration:\n" + "\n".join(stale))
        return 1
    print(f"Privacy documents {'are current' if args.check else 'generated'}: {len(generated)} files.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
