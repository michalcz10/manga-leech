"""Download chapters from RSS or a WeebCentral series, one PDF per chapter."""
from __future__ import annotations

import argparse
import hashlib
import io
import http.cookiejar
import http.client
import logging
import re
import sys
import threading
import time
import unicodedata
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from concurrent.futures import ProcessPoolExecutor, ThreadPoolExecutor, wait, FIRST_COMPLETED
from decimal import Decimal
from email.utils import parsedate_to_datetime
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import urljoin, urlparse

from PIL import Image
from reportlab import rl_config
from reportlab.lib.utils import ImageReader
from reportlab.pdfbase.pdfdoc import PDFDictionary, PDFName, PDFStream
from reportlab.pdfgen import canvas

LOG = logging.getLogger("manga-leech")
# PDF supports binary streams; ASCII85 would add about 25% to embedded JPEGs.
rl_config.useA85 = False
PDF_FORMAT_VERSION = "original-jpeg-series-title-v5"
CHAPTER_LABEL = re.compile(r'\b(?:Chapter|Page|Volume|Vol)\.?\s+(\d+(?:\.\d+)?)(?=\s|$)', re.I)
VOID_TAGS = {"img", "br", "hr", "input", "meta", "link", "source", "wbr", "area", "base", "embed", "param", "track", "col"}


@dataclass(frozen=True)
class Chapter:
    title: str
    url: str


def chapter_sort_key(chapter: Chapter):
    matches = list(CHAPTER_LABEL.finditer(chapter.title))
    match = matches[-1] if matches else None
    return (0, Decimal(match.group(1))) if match else (1, Decimal(0))


def series_metadata(title: str):
    matches = list(CHAPTER_LABEL.finditer(title))
    if matches:
        match = matches[-1]
        series = title[:match.start()].strip()
        if series:
            return series, match.group(1)
    return None, None


def pdf_xmp(title: str) -> bytes:
    ns = {
        'x': 'adobe:ns:meta/',
        'rdf': 'http://www.w3.org/1999/02/22-rdf-syntax-ns#',
        'dc': 'http://purl.org/dc/elements/1.1/',
        'calibre': 'http://calibre-ebook.com/xmp-namespace',
        'calibreSI': 'http://calibre-ebook.com/xmp-namespace-series-index',
    }
    for prefix, uri in ns.items():
        ET.register_namespace(prefix, uri)
    def tag(prefix, name):
        return '{' + ns[prefix] + '}' + name
    root = ET.Element(tag('x', 'xmpmeta'))
    rdf = ET.SubElement(root, tag('rdf', 'RDF'))
    description = ET.SubElement(rdf, tag('rdf', 'Description'), {tag('rdf', 'about'): ''})
    title_node = ET.SubElement(description, tag('dc', 'title'))
    alternative = ET.SubElement(title_node, tag('rdf', 'Alt'))
    series, number = series_metadata(title)
    ET.SubElement(alternative, tag('rdf', 'li'), {'{http://www.w3.org/XML/1998/namespace}lang': 'x-default'}).text = series or title
    if series:
        ET.SubElement(description, tag('calibre', 'series')).text = series
        ET.SubElement(description, tag('calibreSI', 'series_index')).text = number
    return ET.tostring(root, encoding='utf-8', xml_declaration=True)


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
        self.inline_chapters = []
        self.stack = []
        self.anchor_in_list = False

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        inside_list = any(self.stack) or attrs.get('id') == 'chapter-list'
        if tag not in VOID_TAGS:
            self.stack.append(inside_list)
        endpoint = attrs.get("hx-get", "")
        if urlparse(endpoint).path.endswith("/full-chapter-list"):
            self.full_list = endpoint
        if tag == "title":
            self.in_title = True
        if tag == "a":
            self.anchor = attrs.get("href")
            self.text = []
            self.anchor_in_list = inside_list

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
                match = CHAPTER_LABEL.search(text)
                if match:
                    chapter = Chapter(match.group(), self.anchor)
                    self.chapters.append(chapter)
                    if self.anchor_in_list:
                        self.inline_chapters.append(chapter)
            self.anchor = None
        if tag not in VOID_TAGS and self.stack:
            self.stack.pop()


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


def pdf_name(chapter: Chapter) -> str:
    series, number = series_metadata(chapter.title)
    if not series:
        return safe_name(chapter)
    series = unicodedata.normalize('NFKC', series)
    series = re.sub(r'[<>:"/\\|?*\x00-\x1f]', '_', series).strip(' .')[:120] or 'series'
    integer, separator, fraction = number.partition('.')
    number = str(int(integer)).zfill(3) + (separator + fraction if separator else '')
    label = list(CHAPTER_LABEL.finditer(chapter.title))[-1].group()
    kind = 'Vol' if re.match(r'Vol', label, re.I) else 'Ch'
    return f'{series} - {kind} {number}'


class Client:
    def __init__(self, timeout: float, delay: float, user_agent: str = "MangaLeech/0.1"):
        self.timeout, self.delay = timeout, delay
        self.user_agent = user_agent
        self.cookies = http.cookiejar.CookieJar()
        self.local = threading.local()
        self.cooldown_lock = threading.Lock()
        self.cooldowns = {}

    def wait_for_host(self, host):
        while True:
            with self.cooldown_lock:
                remaining = self.cooldowns.get(host, 0) - time.monotonic()
            if remaining <= 0:
                return
            time.sleep(min(remaining, 1))

    def retry_delay(self, headers, fallback):
        value = headers.get("Retry-After") if headers else None
        if value:
            try:
                return max(0, float(value))
            except ValueError:
                try:
                    return max(0, parsedate_to_datetime(value).timestamp() - time.time())
                except (ValueError, TypeError, OverflowError):
                    pass
        return fallback

    def thread_opener(self):
        # Handler state is per thread; CookieJar synchronizes access internally.
        if not hasattr(self.local, "opener"):
            self.local.opener = urllib.request.build_opener(
                urllib.request.HTTPCookieProcessor(self.cookies)
            )
        return self.local.opener

    def get(self, url: str, referer: str | None = None) -> bytes:
        headers = {"User-Agent": self.user_agent, "Accept": "*/*"}
        if referer:
            headers["Referer"] = referer
        host = urlparse(http_url(url)).netloc
        for attempt in range(3):
            self.wait_for_host(host)
            time.sleep(self.delay)
            try:
                started = time.monotonic()
                # Socket timeout alone does not bound a slowly trickling response.
                with self.thread_opener().open(urllib.request.Request(url, headers=headers), timeout=min(10, self.timeout)) as response:
                    chunks = []
                    received = 0
                    while True:
                        if time.monotonic() - started >= self.timeout:
                            raise TimeoutError(f"Prekrocen cas celeho prenosu {self.timeout:g} s ({received} bajtu)")
                        chunk = response.read1(64 * 1024)
                        if not chunk:
                            break
                        chunks.append(chunk)
                        received += len(chunk)
                    expected = response.headers.get("Content-Length")
                    if expected is not None and received != int(expected):
                        raise http.client.IncompleteRead(b"", int(expected) - received)
                    return b"".join(chunks)
            except urllib.error.HTTPError as exc:
                status = exc.code
                pause = self.retry_delay(exc.headers, 2 ** (attempt + 1))
                exc.close()
                LOG.warning("HTTP %d: %s (pokus %d/3)", status, url, attempt + 1)
                if status not in {429, 500, 502, 503, 504} or attempt == 2:
                    raise
                if status in {429, 503}:
                    if pause > 120:
                        LOG.error("Server pozaduje pauzu %.0f s; kapitola selze misto dlouheho cekani. Zkuste pozdeji.", pause)
                        raise
                    with self.cooldown_lock:
                        self.cooldowns[host] = max(self.cooldowns.get(host, 0), time.monotonic() + pause)
                LOG.warning("Opakuji za %.1f s: %s", pause, url)
                time.sleep(pause)
            except (OSError, TimeoutError, http.client.HTTPException) as exc:
                LOG.warning("Chyba prenosu: %s; %s (pokus %d/3)", exc, url, attempt + 1)
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
        if not page.full_list and not page.inline_chapters:
            raise ValueError("Na strance serie chybi odkaz na uplny seznam kapitol; nebudu stahovat pouze nahled.")
        if page.full_list:
            endpoint = http_url(urljoin(url, page.full_list))
            if urlparse(endpoint).netloc != urlparse(url).netloc:
                raise ValueError("Uplny seznam kapitol odkazuje na jinou domenu.")
            listing = SeriesPage()
            listing.feed(self.get(endpoint, url).decode("utf-8-sig"))
            entries = listing.chapters
        else:
            # Short series have their full list inline and no Show All button.
            entries = page.inline_chapters
        name = page.title.split(" | ")[0].strip()
        chapters = {}
        for chapter in entries:
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
        pdf = canvas.Canvas(str(temporary), pageCompression=1)
        pdf.setTitle(series_metadata(title)[0] or title)
        metadata = PDFStream(
            PDFDictionary({'Type': PDFName('Metadata'), 'Subtype': PDFName('XML')}),
            content=pdf_xmp(title), filters=[],
        )
        pdf._doc.Catalog.Metadata = metadata
        for path in images:
            with Image.open(path) as source:
                # One original image per PDF page; keep aspect ratio and avoid PDF size limits.
                width, height = source.size
                scale = min(0.75, 14000 / max(width, height))
                w, h = width * scale, height * scale
                pdf.setPageSize((w, h))
                # Read from the file so ImageReader retains the original JPEG stream.
                # This detects the actual format even for our cached .img filenames.
                pdf.drawImage(ImageReader(str(path)), 0, 0, width=w, height=h, mask="auto")
                pdf.showPage()
        pdf.save()
        temporary.replace(output)
    finally:
        temporary.unlink(missing_ok=True)


def finish_pdf(paths, pdf, title, manifest, signature, legacy_pdf=None):
    """Top-level function so Windows can run it in a spawned process."""
    make_pdf(paths, pdf, title)
    temporary = manifest.with_suffix(".sha256.part")
    temporary.write_text(signature)
    temporary.replace(manifest)
    # Complete the filename migration only after the replacement PDF is ready.
    if legacy_pdf is not None and legacy_pdf != pdf:
        legacy_pdf.unlink(missing_ok=True)
        legacy_pdf.with_suffix('.pages.sha256').unlink(missing_ok=True)
    return pdf


def download_chapter(client: Client, chapter: Chapter, output: Path, force: bool, workers: int = 4, pdf_executor=None):
    if workers < 1:
        raise ValueError("Pocet vlaken musi byt alespon 1")
    name = safe_name(chapter)
    pdf = output / f"{pdf_name(chapter)}.pdf"
    legacy_pdf = output / f"{name}.pdf"
    urls = image_urls(client.chapter_html(chapter.url), chapter.url)
    signature = hashlib.sha256((PDF_FORMAT_VERSION + "\n" + chapter.title + "\n" + "\n".join(urls)).encode()).hexdigest()
    manifest = pdf.with_suffix('.pages.sha256')
    if pdf.exists() and manifest.exists() and manifest.read_text() == signature and not force:
        LOG.info("Preskakuji existujici PDF: %s", pdf.name)
        return
    cache = output / ".images" / name
    cache.mkdir(parents=True, exist_ok=True)
    def download_image(index, url):
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
        return path

    paths = [None] * len(urls)
    with ThreadPoolExecutor(max_workers=workers, thread_name_prefix="image") as executor:
        pending = {executor.submit(download_image, index, url): index for index, url in enumerate(urls, 1)}
        try:
            completed = 0
            remaining = set(pending)
            while remaining:
                done, remaining = wait(remaining, timeout=10, return_when=FIRST_COMPLETED)
                if not done:
                    indices = sorted(pending[f] for f in remaining)
                    LOG.warning("%s: stale cekam na %d obrazku; cisla %s", chapter.title, len(indices), indices[:12])
                for future in done:
                    index = pending[future]
                    try:
                        paths[index - 1] = future.result()
                    except Exception as exc:
                        raise RuntimeError(f"Obrazek {index}/{len(urls)} ({urls[index - 1]}): {exc}") from exc
                    completed += 1
                    LOG.info("%s: hotovo %d/%d (obrazek %d)", chapter.title, completed, len(urls), index)
        except BaseException:
            for future in pending:
                future.cancel()
            raise
    if pdf_executor is not None:
        LOG.info("Predavam do tvorby PDF: %s", chapter.title)
        return pdf_executor.submit(finish_pdf, paths, pdf, chapter.title, manifest, signature, legacy_pdf)
    finish_pdf(paths, pdf, chapter.title, manifest, signature, legacy_pdf)
    LOG.info("Hotovo: %s", pdf)


def run_downloads(client, chapters, output, force, workers, pdf_workers):
    names = {}
    for chapter in chapters:
        name = pdf_name(chapter).casefold()
        if name in names and names[name] != chapter.url:
            raise ValueError(f'Dve kapitoly maji stejny vystupni nazev: {pdf_name(chapter)}.pdf')
        names[name] = chapter.url
    failures = 0
    pending = {}

    def collect(block=False):
        nonlocal failures
        done = wait(pending, return_when=FIRST_COMPLETED).done if block else {f for f in pending if f.done()}
        for future in done:
            title = pending.pop(future)
            try:
                LOG.info("Hotovo: %s", future.result())
            except Exception as exc:
                failures += 1
                LOG.error("Tvorba PDF %s selhala: %s", title, exc)

    with ProcessPoolExecutor(max_workers=pdf_workers) as executor:
        try:
            for chapter in chapters:
                collect()
                # Bound queued chapters; pass paths rather than image bytes to workers.
                while len(pending) >= pdf_workers * 2:
                    LOG.info("Cekam na uvolneni fronty PDF...")
                    collect(block=True)
                try:
                    future = download_chapter(client, chapter, output, force, workers, executor)
                    if future is not None:
                        pending[future] = chapter.title
                except Exception as exc:
                    failures += 1
                    LOG.error("Kapitola %s selhala: %s", chapter.title, exc)
            if pending:
                LOG.info("Stahovani dokonceno, cekam na zbyvajici PDF...")
            while pending:
                collect(block=True)
        except BaseException:
            for future in pending:
                future.cancel()
            raise
    return failures


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
    parser.add_argument("--timeout", type=float, default=60, help="Limit jednoho pokusu o prenos v sekundach (vychozi 60)")
    parser.add_argument("--workers", type=int, default=4, help="Pocet soubezne stahovanych obrazku (vychozi 4, 1 = postupne)")
    parser.add_argument("--pdf-workers", type=int, default=1, help="Pocet procesu pro PDF bezici soubezne se stahovanim (vychozi 1)")
    parser.add_argument("--delay", type=float, default=0.5, help="Prodleva pred pozadavkem v kazdem vlakne v sekundach")
    args = parser.parse_args(argv)
    if bool(args.feed) == bool(args.chapter):
        parser.error("Zadejte RSS / stranku serie, nebo --chapter URL")
    if args.oldest_first and args.newest_first:
        parser.error("Vyberte pouze jeden smer razeni")
    if args.timeout <= 0 or args.delay < 0 or args.workers < 1 or args.pdf_workers < 1 or (args.limit is not None and args.limit <= 0):
        parser.error("timeout, limit, workers a pdf-workers musi byt kladne, delay nezaporne")
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s", datefmt="%H:%M:%S")
    client = None
    try:
        client = Client(args.timeout, args.delay, args.user_agent)
        if args.chapter:
            url = http_url(args.chapter)
            page = SeriesPage()
            page.feed(client.get(url).decode('utf-8-sig'))
            parts = [part.strip() for part in page.title.split(' | ')]
            if len(parts) >= 3 and CHAPTER_LABEL.fullmatch(parts[0]):
                title = f'{parts[1]} {parts[0]}'
            else:
                title = page.title.removesuffix(' | Weeb Central').strip()
            if not title:
                raise ValueError('Nelze zjistit nazev kapitoly pro metadata PDF.')
            chapters = [Chapter(title, url)]
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
        LOG.info("Stahovani pomoci %d vlaken.", args.workers)
        failures = run_downloads(client, chapters, args.output, args.force, args.workers, args.pdf_workers)
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
