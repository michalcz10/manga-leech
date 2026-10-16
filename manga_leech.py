"""Download chapters from RSS or a WeebCentral series, one PDF per chapter."""
from __future__ import annotations

import argparse
import hashlib
import io
import http.cookiejar
import logging
import re
import sys
import time
import unicodedata
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from decimal import Decimal
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import urljoin, urlparse

from PIL import Image
from reportlab.lib.utils import ImageReader
from reportlab.pdfgen import canvas

LOG = logging.getLogger("manga-leech")
VOID_TAGS = {"img", "br", "hr", "input", "meta", "link", "source", "wbr", "area", "base", "embed", "param", "track", "col"}


@dataclass(frozen=True)
class Chapter:
    title: str
    url: str


def chapter_sort_key(chapter: Chapter):
    match = re.search(r'\bchapter\s+(\d+(?:\.\d+)?)', chapter.title, re.I)
    return (0, Decimal(match.group(1))) if match else (1, Decimal(0))


def http_url(value: str) -> str:
    if urlparse(value).scheme not in {"http", "https"} or not urlparse(value).hostname:
        raise ValueError(f"Neplatna HTTP(S) adresa: {value}")
    return value


def parse_feed(data: bytes, base: str) -> list[Chapter]:
    root = ET.fromstring(data)
    chapters = []
    seen = set()
    # RSS 2.0 and Atom, preserving the feed's order.
    for item in root.iter():
        if item.tag.split("}")[-1] not in {"item", "entry"}:
            continue
        title, link = "", ""
        for child in item:
            tag = child.tag.split("}")[-1]
            if tag == "title":
                title = "".join(child.itertext()).strip()
            elif tag == "link" and child.get("rel", "alternate") == "alternate":
                link = child.get("href") or (child.text or "").strip()
        if not link:
            continue
        url = http_url(urljoin(base, link))
        if url not in seen:
            seen.add(url)
            chapters.append(Chapter(title or urlparse(url).path.rsplit("/", 1)[-1], url))
    if not chapters:
        raise ValueError("Feed neobsahuje zadne kapitoly s odkazem.")
    return chapters


class SeriesPage(HTMLParser):
    """Read the public full-list link, document title and chapter anchors."""

    def __init__(self):
        super().__init__()
        self.full_list = None
        self.title = ""
        self.in_title = False
        self.anchor = None
        self.text = []
        self.chapters = []

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        endpoint = attrs.get("hx-get", "")
        if urlparse(endpoint).path.endswith("/full-chapter-list"):
            self.full_list = endpoint
        if tag == "title":
            self.in_title = True
        if tag == "a":
            self.anchor = attrs.get("href")
            self.text = []

    def handle_data(self, data):
        if self.in_title:
            self.title += data
        if self.anchor:
            self.text.append(data)

    def handle_endtag(self, tag):
        if tag == "title":
            self.in_title = False
        if tag == "a":
            if self.anchor and re.fullmatch(r'/chapters/[^/]+', urlparse(self.anchor).path):
                text = " ".join(" ".join(self.text).split())
                match = re.search(r'\bChapter\s+\d+(?:\.\d+)?', text, re.I)
                if match:
                    self.chapters.append(Chapter(match.group(), self.anchor))
            self.anchor = None


class ReaderImages(HTMLParser):
    def __init__(self):
        super().__init__()
        self.stack = []
        self.urls = []

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        active = any(self.stack) or attrs.get("id") == "chapter-images"
        if tag == "img" and active:
            src = attrs.get("data-src") or attrs.get("src")
            if src:
                self.urls.append(src)
        if tag not in VOID_TAGS:
            self.stack.append(active)

    def handle_endtag(self, tag):
        # HTMLParser calls handle_endtag for XHTML-style <img ... /> too.
        # Void elements never opened a stack frame, so must not close the reader.
        if tag not in VOID_TAGS and self.stack:
            self.stack.pop()


def image_urls(html: str, base: str) -> list[str]:
    parser = ReaderImages()
    parser.feed(html)
    urls = list(dict.fromkeys(http_url(urljoin(base, src)) for src in parser.urls))
    if not urls:
        raise ValueError("V #chapter-images nejsou obrazky. Server mohl vratit jinou stranku nebo se zmenila struktura webu.")
    return urls


def safe_name(chapter: Chapter) -> str:
    title = unicodedata.normalize("NFKC", chapter.title)
    title = re.sub(r'[<>:"/\\|?*\x00-\x1f]', "_", title).strip(" .")[:120] or "chapter"
    return f"{title}-{hashlib.sha256(chapter.url.encode()).hexdigest()[:10]}"


class Client:
    def __init__(self, timeout: float, delay: float, user_agent: str = "MangaLeech/0.1"):
        self.timeout, self.delay = timeout, delay
        self.user_agent = user_agent
        self.opener = urllib.request.build_opener(
            urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar())
        )

    def get(self, url: str, referer: str | None = None) -> bytes:
        headers = {"User-Agent": self.user_agent, "Accept": "*/*"}
        if referer:
            headers["Referer"] = referer
        for attempt in range(3):
            time.sleep(self.delay)
            try:
                with self.opener.open(urllib.request.Request(http_url(url), headers=headers), timeout=self.timeout) as response:
                    return response.read()
            except urllib.error.HTTPError as exc:
                if exc.code not in {429, 500, 502, 503, 504} or attempt == 2:
                    raise
                time.sleep(2 ** (attempt + 1))
            except (OSError, TimeoutError):
                if attempt == 2:
                    raise
                time.sleep(2 ** (attempt + 1))
        raise RuntimeError("Stazeni selhalo")

    def chapter_html(self, url: str) -> str:
        html = self.get(url).decode("utf-8-sig")
        # WeebCentral loads the reader as an HTML fragment using a normal GET.
        if not re.search(r'id=["\']chapter-images["\'][^>]*>\s*<img', html):
            match = re.search(r'["\']([^"\']+/images\?is_prev=False)["\']', html)
            if match:
                endpoint = urljoin(url, match.group(1))
                if urlparse(endpoint).netloc != urlparse(url).netloc:
                    raise ValueError("Seznam obrazku odkazuje na jinou domenu.")
                return self.get(endpoint + "&reading_style=long_strip&current_page=1", url).decode("utf-8-sig")
        return html

    def feed_bytes(self, url: str) -> bytes:
        return self.get(url)

    def series_chapters(self, url: str) -> list[Chapter]:
        page = SeriesPage()
        page.feed(self.get(url).decode("utf-8-sig"))
        if not page.full_list:
            raise ValueError("Na strance serie chybi odkaz na uplny seznam kapitol; nebudu stahovat pouze nahled.")
        endpoint = http_url(urljoin(url, page.full_list))
        if urlparse(endpoint).netloc != urlparse(url).netloc:
            raise ValueError("Uplny seznam kapitol odkazuje na jinou domenu.")
        listing = SeriesPage()
        listing.feed(self.get(endpoint, url).decode("utf-8-sig"))
        name = page.title.split(" | ")[0].strip()
        chapters = {}
        for chapter in listing.chapters:
            link = http_url(urljoin(url, chapter.url))
            if urlparse(link).netloc != urlparse(url).netloc:
                raise ValueError("Kapitola odkazuje na jinou domenu.")
            chapters.setdefault(link, Chapter(f"{name} {chapter.title}".strip(), link))
        if not chapters:
            raise ValueError("Uplny seznam serie neobsahuje zadne kapitoly.")
        return list(chapters.values())


def valid_image(path: Path) -> bool:
    try:
        with Image.open(path) as image:
            image.verify()
        return True
    except (OSError, ValueError):
        return False


def make_pdf(images: list[Path], output: Path, title: str):
    temporary = output.with_suffix(".pdf.part")
    try:
        pdf = canvas.Canvas(str(temporary))
        pdf.setTitle(title)
        for path in images:
            with Image.open(path) as source:
                image = source.convert("RGB")
                # One original image per PDF page; keep aspect ratio and avoid PDF size limits.
                width, height = image.size
                scale = min(0.75, 14000 / max(width, height))
                w, h = width * scale, height * scale
                pdf.setPageSize((w, h))
                pdf.drawImage(ImageReader(image), 0, 0, width=w, height=h)
                pdf.showPage()
                image.close()
        pdf.save()
        temporary.replace(output)
    finally:
        temporary.unlink(missing_ok=True)


def download_chapter(client: Client, chapter: Chapter, output: Path, force: bool):
    name = safe_name(chapter)
    pdf = output / f"{name}.pdf"
    urls = image_urls(client.chapter_html(chapter.url), chapter.url)
    signature = hashlib.sha256("\n".join(urls).encode()).hexdigest()
    manifest = output / f"{name}.pages.sha256"
    if pdf.exists() and manifest.exists() and manifest.read_text() == signature and not force:
        LOG.info("Preskakuji existujici PDF: %s", pdf.name)
        return
    cache = output / ".images" / name
    cache.mkdir(parents=True, exist_ok=True)
    paths = []
    for index, url in enumerate(urls, 1):
        key = hashlib.sha256(url.encode()).hexdigest()[:12]
        path = cache / f"{index:05d}-{key}.img"
        if force or not valid_image(path):
            data = client.get(url, chapter.url)
            # Verify before committing the cache entry.
            with Image.open(io.BytesIO(data)) as image:
                image.verify()
            temporary = path.with_suffix(".part")
            temporary.write_bytes(data)
            temporary.replace(path)
        paths.append(path)
        LOG.info("%s: obrazek %d/%d", chapter.title, index, len(urls))
    make_pdf(paths, pdf, chapter.title)
    manifest.write_text(signature)
    LOG.info("Hotovo: %s", pdf)


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description="RSS nebo stranka serie -> jedno PDF pro kazdou kapitolu (WeebCentral).")
    parser.add_argument("feed", nargs="?", help="HTTP(S) adresa RSS/Atom feedu nebo stranky serie WeebCentral")
    parser.add_argument("--chapter", help="Stahnout jednu kapitolu primo podle URL misto RSS")
    parser.add_argument("-o", "--output", type=Path, default=Path("downloads"))
    parser.add_argument("--user-agent", default="MangaLeech/0.1", help="Hodnota HTTP hlavicky User-Agent")
    parser.add_argument("--list", action="store_true", help="Pouze vypsat kapitoly")
    parser.add_argument("--limit", type=int, help="Zpracovat prvnich N polozek feedu")
    parser.add_argument("--oldest-first", action="store_true", help="Radit od nejnizsiho cisla kapitoly (vychozi)")
    parser.add_argument("--newest-first", action="store_true", help="Radit od nejvyssiho cisla kapitoly")
    parser.add_argument("--force", action="store_true", help="Znovu stahnout i hotove kapitoly")
    parser.add_argument("--timeout", type=float, default=60)
    parser.add_argument("--delay", type=float, default=0.5, help="Prodleva mezi pozadavky v sekundach")
    args = parser.parse_args(argv)
    if bool(args.feed) == bool(args.chapter):
        parser.error("Zadejte RSS / stranku serie, nebo --chapter URL")
    if args.oldest_first and args.newest_first:
        parser.error("Vyberte pouze jeden smer razeni")
    if args.timeout <= 0 or args.delay < 0 or (args.limit is not None and args.limit <= 0):
        parser.error("timeout a limit musi byt kladne, delay nezaporne")
    logging.basicConfig(level=logging.INFO, format="%(message)s")
    client = None
    try:
        client = Client(args.timeout, args.delay, args.user_agent)
        if args.chapter:
            url = http_url(args.chapter)
            chapters = [Chapter("Chapter " + urlparse(url).path.rstrip("/").rsplit("/", 1)[-1], url)]
        else:
            source = http_url(args.feed)
            path = urlparse(source).path.rstrip("/")
            if path.startswith("/series/") and not path.endswith("/rss"):
                chapters = client.series_chapters(source)
                LOG.info("Uplny seznam serie obsahuje %d kapitol.", len(chapters))
            else:
                chapters = parse_feed(client.feed_bytes(source), source)
                LOG.info("Feed obsahuje %d kapitol.", len(chapters))
            chapters.sort(key=chapter_sort_key, reverse=args.newest_first)
        chapters = chapters[:args.limit]
        if args.list:
            for chapter in chapters:
                print(f"{chapter.title}\t{chapter.url}")
            return 0
        args.output.mkdir(parents=True, exist_ok=True)
        failures = 0
        for chapter in chapters:
            try:
                download_chapter(client, chapter, args.output, args.force)
            except Exception as exc:
                failures += 1
                LOG.error("Kapitola %s selhala: %s", chapter.title, exc)
        return 1 if failures else 0
    except KeyboardInterrupt:
        LOG.error("Preruseno. Dalsi spusteni navaze na stazene obrazky.")
        return 130
    except Exception as exc:
        LOG.error("Chyba: %s", exc)
        if isinstance(exc, urllib.error.HTTPError) and exc.code == 403:
            LOG.error("Server odmitl HTTP pozadavek (403). Zmena klienta na curl/wget sama o sobe nezarucuje pristup.")
        return 1


if __name__ == "__main__":
    sys.exit(main())
