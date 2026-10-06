"""Build the browser manuals shipped with the app. Requires markdown-it-py."""
from pathlib import Path
from html import escape
import re
from markdown_it import MarkdownIt

ROOT = Path(__file__).resolve().parent.parent
DOCS = ROOT / "docs"
PAGES = ["MANUAL.ja", "MANUAL.en", "SETTINGS.ja", "SETTINGS.en", "TROUBLESHOOTING.ja",
         "COMPATIBILITY.ja", "PRIVACY.ja", "OVERLAY.ja", "MONITOR.ja", "REPORTING.ja"]
CSS = """+:root{color-scheme:light;--ink:#172b3a;--muted:#586c78;--line:#dae5ea;--accent:#006d77}
*{box-sizing:border-box}html{scroll-behavior:smooth;scroll-padding-top:28px}body{margin:0;background:#f3f7f8;color:var(--ink);font:16px/1.85 'Segoe UI','Yu Gothic UI',Meiryo,sans-serif}
a{color:var(--accent);text-underline-offset:4px}a:hover{color:#00464d}a:focus-visible,summary:focus-visible{outline:3px solid #008f9c;outline-offset:5px}header{background:#112630;color:#fff;padding:24px max(24px,calc((100vw - 1160px)/2));display:flex;align-items:center;justify-content:space-between;gap:20px}header a{color:#bfe8e8}header strong{font-size:19px;letter-spacing:.12em}header small{display:block;color:#afc4cd;font-size:12px;letter-spacing:.05em}nav{display:flex;gap:22px;font-size:14px}
.shell{max-width:1160px;margin:40px auto 64px;padding:0 24px;display:grid;grid-template-columns:220px minmax(0,1fr);gap:40px}aside{position:sticky;top:24px;align-self:start;font-size:14px}aside p{font-weight:700;font-size:12px;letter-spacing:.12em;color:var(--muted)}aside a{display:block;margin:0 0 10px;text-decoration:none;color:var(--muted);line-height:1.55}aside a:hover{color:var(--accent)}main{background:white;border:1px solid var(--line);border-radius:18px;padding:38px 44px;min-width:0}h1{font-size:32px;line-height:1.4;letter-spacing:-.02em;margin:0 0 28px}h2{font-size:22px;margin:42px 0 16px;padding-top:28px;border-top:1px solid var(--line);line-height:1.5}h3{font-size:18px;margin:28px 0 12px}p{margin:14px 0}li{margin:10px 0}strong{font-weight:700}table{border-collapse:collapse;width:100%;font-size:14px;margin:22px 0}th,td{padding:13px 15px;border-bottom:1px solid var(--line);text-align:left;vertical-align:top}th{background:#f0f6f7}code{background:#edf3f5;border-radius:4px;padding:2px 5px;font: .9em Consolas,monospace;overflow-wrap:anywhere}pre{overflow:auto;background:#112630;color:#e0f0f3;padding:20px;border-radius:10px}pre code{background:transparent;color:inherit;padding:0}img{max-width:100%;height:auto}blockquote{margin:24px 0;border-left:4px solid #0097a3;background:#ecf8f8;padding:6px 22px;border-radius:0 8px 8px 0}.quick{background:#eaf7f6;border:1px solid #c6e7e4;border-radius:12px;padding:22px 26px;margin-bottom:28px}.quick strong{font-size:17px}.quick ol{margin-bottom:0;padding-left:22px}.button{display:inline-block;background:#006d77;color:white;padding:10px 18px;border-radius:8px;text-decoration:none;font-weight:600;margin:4px 8px 4px 0}.button:hover{background:#004f58;color:white}footer{color:var(--muted);font-size:13px;margin:40px 0 0;padding-top:20px;border-top:1px solid var(--line)}
@media(max-width:820px){header{padding:18px 22px;flex-wrap:wrap}nav{gap:16px}.shell{display:block;margin:24px auto;padding:0 16px}aside{position:static;margin-bottom:24px}aside a{display:inline-block;margin:0 16px 10px 0}main{padding:26px 22px;border-radius:12px}h1{font-size:28px}h2{font-size:21px}table{font-size:13px}th,td{padding:10px 8px}}
"""

def build(name):
    en = name.endswith(".en")
    source = (DOCS / (name + ".md")).read_text(encoding="utf-8-sig")
    md = MarkdownIt("commonmark", {"html": False}).enable("table")
    tokens = md.parse(source)
    toc = []
    heading_number = 0
    for i, token in enumerate(tokens):
        if token.type == "heading_open" and token.tag in ("h2", "h3"):
            title = tokens[i + 1].content
            heading_number += 1
            anchor = f"section-{heading_number}"
            token.attrSet("id", anchor)
            if token.tag == "h2":
                toc.append((anchor, title))
        for child in token.children or []:
            if child.type == "link_open":
                href = child.attrGet("href") or ""
                if href.startswith("../") and href.endswith(".md"):
                    child.attrSet("href", "https://github.com/anyscape-maruo/fc27-pad-lock/blob/main/" + href[3:])
                elif re.fullmatch(r"[A-Z]+\.(ja|en)\.md(?:#.*)?", href):
                    child.attrSet("href", href.replace(".md", ".html"))
    title = next(t.content for t in tokens if t.type == "inline")
    body = md.renderer.render(tokens, md.options, {})
    quick = ""
    if name.startswith("MANUAL"):
        quick = ("<section class='quick'><strong>Start here</strong><ol><li>Install FC27 Pad Lock.</li><li>Open the app. Protection starts automatically.</li><li>Start FC27 and play with your controller.</li></ol></section>" if en else
                 "<section class='quick'><strong>まずは、この3つだけ</strong><ol><li>FC27 Pad Lockをインストールする。</li><li>アプリを開く。保護は自動で有効になります。</li><li>FC27を起動し、コントローラーで遊ぶ。</li></ol></section>")
    manual = "MANUAL.en.html" if en else "MANUAL.ja.html"
    labels = ("Manual", "Download", "On this page", "An offline manual. GitHub links require internet access.") if en else ("使い方", "ダウンロード", "このページの内容", "このマニュアルはネット接続なしで読めます。GitHubのリンク先はネット接続が必要です。")
    nav = f"<a href='{manual}'>{labels[0]}</a><a href='https://github.com/anyscape-maruo/fc27-pad-lock/releases'>{labels[1]}</a><a href='{'MANUAL.ja.html' if en else 'MANUAL.en.html'}'>{'日本語' if en else 'English'}</a>"
    contents = "".join(f"<a href='#{a}'>{escape(t)}</a>" for a,t in toc)
    html = f"<!doctype html><html lang='{'en' if en else 'ja'}'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>{escape(title)} | FC27 Pad Lock</title><style>{CSS}</style></head><body><header><div><strong>PAD LOCK</strong><small>FC27 · {'HELP & GUIDES' if en else '使い方・サポート'}</small></div><nav aria-label='{'Guides' if en else 'ガイド'}'>{nav}</nav></header><div class='shell'><aside aria-label='{'Table of contents' if en else '目次'}'><p>{labels[2]}</p>{contents}</aside><main>{quick}{body}<footer>{labels[3]}</footer></main></div></body></html>"
    (DOCS / (name + ".html")).write_text(html, encoding="utf-8")

for page in PAGES:
    build(page)
landing = f"<!doctype html><html lang='ja'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>FC27 Pad Lock マニュアル</title><style>{CSS}</style></head><body><header><div><strong>PAD LOCK</strong><small>FC27 · 使い方・サポート</small></div></header><div class='shell' style='display:block;max-width:820px'><main><h1>FC27 Pad Lockの使い方</h1><p>始め方、表示の設定、困ったときの対処をブラウザで読めます。</p><a class='button' href='docs/MANUAL.ja.html'>マニュアルを開く</a><a class='button' href='docs/MANUAL.en.html'>English manual</a><p><a href='https://github.com/anyscape-maruo/fc27-pad-lock/releases'>最新版をダウンロード</a></p></main></div></body></html>"
(ROOT / "Manual.html").write_text(landing, encoding="utf-8")
(DOCS / "index.html").write_text((DOCS / "MANUAL.ja.html").read_text(encoding="utf-8"), encoding="utf-8")
(DOCS / ".nojekyll").touch()
print(f"Built {len(PAGES)} browser guides, Manual.html and the Pages entry point")
