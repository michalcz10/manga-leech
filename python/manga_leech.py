"""Stahování mangy z RSS nebo stránky série WeebCentral do samostatných PDF.

Průběh: vstupní URL -> seznam vydání -> obrázky v cache -> PDF s metadaty.
Obrázky jedné kapitoly stahují vlákna; PDF skládají samostatné procesy,
takže stahování další kapitoly může pokračovat během tvorby předchozího PDF.
"""
from __future__ import annotations

import argparse
import gc
import hashlib
import io
import http.cookiejar
import http.client
import logging
import math
import multiprocessing
import re
import struct
import sys
import threading
import time
import unicodedata
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from contextlib import nullcontext
from concurrent.futures import ProcessPoolExecutor, ThreadPoolExecutor, wait, FIRST_COMPLETED
from decimal import Decimal
from email.utils import parsedate_to_datetime
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import urljoin, urlparse

from PIL import Image
import requests
from urllib3.exceptions import HTTPError as TransportError
from reportlab import rl_config
from reportlab.lib.utils import ImageReader
from reportlab.pdfbase.pdfdoc import PDFArray, PDFDictionary, PDFName, PDFStream, PDFString, PDFImageXObject
from reportlab.pdfgen import canvas

LOG = logging.getLogger("manga-leech")
# PDF umí binární streamy; ASCII85 by zbytečně zvětšilo vložené JPEG asi o 25 %.
rl_config.useA85 = False
# Při změně formátu zvýšíme verzi: stará PDF se přestaví z existující cache.
PDF_FORMAT_VERSION = "lossless-png-dedup-v6"
IMAGE_CHECK_VERSION = "decoded-v1"
# Jednotný filtr vydání pro seznam série, RSS, řazení, názvy souborů a metadata.
# Episode i německé Kapitel se zpracují jako kapitola (Ch); Volume/Vol používá Vol.
CHAPTER_LABEL = re.compile(r'\b(?:Chapter|Page|Volume|Vol|Episode|Kapitel)\.?\s+(\d+(?:\.\d+)?)(?=\s|$)', re.I)
# Tyto HTML značky nemají obsah ani vlastní zavírací značku.
VOID_TAGS = {"img", "br", "hr", "input", "meta", "link", "source", "wbr", "area", "base", "embed", "param", "track", "col"}


@dataclass(frozen=True)
class Chapter:
    """Jedno vydání (kapitola, položka Page nebo svazek) a odkaz na jeho čtečku."""
    title: str
    url: str


def chapter_sort_key(chapter: Chapter):
    """Vrátí číselné pořadí; nerozpoznané názvy řadí za očíslovaná vydání."""
    # Decimal zachová pořadí 2, 2.5, 10; poslední shoda patří označení vydání.
    matches = list(CHAPTER_LABEL.finditer(chapter.title))
    match = matches[-1] if matches else None
    return (0, Decimal(match.group(1))) if match else (1, Decimal(0))


def series_metadata(title: str):
    """Oddělí název série od posledního označení vydání, např. Overgeared Chapter 25."""
    matches = list(CHAPTER_LABEL.finditer(title))
    if matches:
        match = matches[-1]
        series = title[:match.start()].strip()
        if series:
            return series, match.group(1)
    return None, None


def pdf_xmp(title: str) -> bytes:
    """Sestaví XMP: titul a série mají stejný název, číslo je index Calibre pro Kavitu."""
    # ElementTree zajistí správné escapování názvů obsahujících například & nebo <.
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
    """Povolí jen absolutní HTTP(S) adresy, nikoli lokální soubory či jiné protokoly."""
    if urlparse(value).scheme not in {"http", "https"} or not urlparse(value).hostname:
        raise ValueError(f"Neplatna HTTP(S) adresa: {value}")
    return value


def parse_feed(data: bytes, base: str) -> list[Chapter]:
    """Načte RSS/Atom, doplní relativní odkazy a odstraní duplicity podle URL."""
    root = ET.fromstring(data)
    chapters = []
    seen = set()
    # Zde zachováváme pořadí feedu; číselné řazení provede až main před --limit.
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
    """Z HTML získá titul série, odkaz Show All a odkazy na jednotlivá vydání.

    chapters obsahuje všechny nalezené odkazy na vydání, inline_chapters pouze
    odkazy uvnitř #chapter-list. To brání přimíchání doporučených sérií.
    """

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
        # Zásobník sleduje, zda aktuální značka leží uvnitř seznamu vydání.
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
    """Sbírá pouze obrázky čtečky #chapter-images, bez loga, reklam a obálek."""
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
        # HTMLParser zavolá handle_endtag také pro <img ... />.
        # Obrázek nepřidal položku do zásobníku, proto nesmí uzavřít sekci čtečky.
        if tag not in VOID_TAGS and self.stack:
            self.stack.pop()


def image_urls(html: str, base: str) -> list[str]:
    """Vrátí unikátní absolutní URL obrázků v původním pořadí čtečky."""
    parser = ReaderImages()
    parser.feed(html)
    urls = list(dict.fromkeys(http_url(urljoin(base, src)) for src in parser.urls))
    if not urls:
        raise ValueError("V #chapter-images nejsou obrazky. Server mohl vratit jinou stranku nebo se zmenila struktura webu.")
    return urls


def safe_name(chapter: Chapter) -> str:
    """Stabilní název interní cache; hash URL rozliší vydání se stejným titulkem."""
    title = unicodedata.normalize("NFKC", chapter.title)
    title = re.sub(r'[<>:"/\\|?*\x00-\x1f]', "_", title).strip(" .")[:120] or "chapter"
    return f"{title}-{hashlib.sha256(chapter.url.encode()).hexdigest()[:10]}"


def pdf_name(chapter: Chapter) -> str:
    """Čitelný název PDF bez přípony: Série - Ch 025 nebo Série - Vol 001."""
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


class PooledResponse:
    """Streamovaná odpověď: read1 zachová kontrolu času i při pomalém přenosu."""
    def __init__(self, response):
        self.response = response
        self.headers = response.headers

    def read1(self, size):
        try:
            return self.response.raw.read1(size, decode_content=False)
        except TransportError as exc:
            raise OSError(str(exc)) from exc

    def __enter__(self):
        return self

    def __exit__(self, *args):
        self.response.close()


class PooledOpener:
    """Jedna Session na vlákno; cookies jsou společné celému klientu."""
    def __init__(self, cookies):
        self.session = requests.Session()
        self.session.cookies = cookies
        for scheme in ('http://', 'https://'):
            self.session.mount(scheme, requests.adapters.HTTPAdapter(
                pool_connections=4, pool_maxsize=1, pool_block=True, max_retries=0))

    def open(self, request, timeout):
        try:
            response = self.session.get(request.full_url, headers=dict(request.header_items()),
                                        timeout=timeout, stream=True)
        except requests.RequestException as exc:
            raise OSError(str(exc)) from exc
        if response.status_code >= 400:
            response.close()
            raise urllib.error.HTTPError(response.url, response.status_code,
                                         response.reason, response.headers, None)
        return PooledResponse(response)

    def close(self):
        self.session.close()


class Client:
    """HTTP klient se sdílenými cookies, opakováním požadavků a pauzami pro hosty."""
    def __init__(self, timeout: float, delay: float, user_agent: str = "MangaLeech/0.1"):
        self.timeout, self.delay = timeout, delay
        self.user_agent = user_agent
        self.cookies = http.cookiejar.CookieJar()
        self.local = threading.local()
        self.cooldown_lock = threading.Lock()
        self.cooldowns = {}
        self.openers = []
        self.openers_lock = threading.Lock()
        self.prefetched_html = {}

    def wait_for_host(self, host):
        """Počká na konec společné pauzy hostu; zámek nedrží během spánku."""
        while True:
            with self.cooldown_lock:
                remaining = self.cooldowns.get(host, 0) - time.monotonic()
            if remaining <= 0:
                return
            time.sleep(min(remaining, 1))

    def retry_delay(self, headers, fallback):
        """Rozpozná Retry-After jako sekundy nebo HTTP datum, jinak použije fallback."""
        value = headers.get("Retry-After") if headers else None
        if value:
            try:
                seconds = float(value)
                return max(0, seconds) if math.isfinite(seconds) else fallback
            except ValueError:
                try:
                    return max(0, parsedate_to_datetime(value).timestamp() - time.time())
                except (ValueError, TypeError, OverflowError):
                    pass
        return fallback

    def thread_opener(self):
        if not hasattr(self.local, "opener"):
            self.local.opener = PooledOpener(self.cookies)
            with self.openers_lock:
                self.openers.append(self.local.opener)
        return self.local.opener

    def close(self):
        """Volat až po doběhnutí download vláken."""
        for opener in self.openers:
            opener.close()
        self.openers.clear()

    def get(self, url: str, referer: str | None = None) -> bytes:
        with io.BytesIO() as target:
            self._transfer(url, referer, target)
            return target.getvalue()

    def download(self, url: str, path: Path, referer: str | None = None):
        """Velký obrázek zapisuje přímo do dočasného souboru, bez kopie v RAM."""
        with path.open('wb') as target:
            self._transfer(url, referer, target)

    def _transfer(self, url, referer, target):
        """Stáhne odpověď s nejvýše třemi pokusy; neúplné přenosy nepřijme."""
        headers = {"User-Agent": self.user_agent, "Accept": "*/*", "Accept-Encoding": "identity"}
        if referer:
            headers["Referer"] = referer
        host = urlparse(http_url(url)).netloc
        for attempt in range(3):
            time.sleep(self.delay)
            self.wait_for_host(host)
            target.seek(0)
            target.truncate()
            try:
                started = time.monotonic()
                # Socket timeout hlídá nečinnost, monotonic navíc celkovou dobu přenosu.
                # Jedno blokující čtení může celkový limit překročit o svůj timeout.
                with self.thread_opener().open(urllib.request.Request(url, headers=headers), timeout=min(10, self.timeout)) as response:
                    received = 0
                    while True:
                        if time.monotonic() - started >= self.timeout:
                            raise TimeoutError(f"Prekrocen cas celeho prenosu {self.timeout:g} s ({received} bajtu)")
                        chunk = response.read1(64 * 1024)
                        if not chunk:
                            break
                        target.write(chunk)
                        received += len(chunk)
                    expected = response.headers.get("Content-Length")
                    if expected is not None and received != int(expected):
                        raise http.client.IncompleteRead(b"", int(expected) - received)
                    return
            except urllib.error.HTTPError as exc:
                status = exc.code
                pause = self.retry_delay(exc.headers, 2 ** (attempt + 1))
                exc.close()
                LOG.warning("HTTP %d: %s (pokus %d/3)", status, url, attempt + 1)
                if status not in {429, 500, 502, 503, 504} or attempt == 2:
                    raise
                if pause > 120:
                    LOG.error("Server pozaduje pauzu %.0f s; kapitola selze misto dlouheho cekani. Zkuste pozdeji.", pause)
                    raise
                # Pauza platí i pro další požadavky ostatních vláken na stejný host.
                if status in {429, 503}:
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
        """Načte čtečku a případně její HTML fragment s kompletním seznamem obrázků."""
        html = getattr(self, 'prefetched_html', {}).pop(url, None)
        if html is None:
            html = self.get(url).decode("utf-8-sig")
        # Stejný GET jako v čtečce webu; pro získání fragmentu není třeba JavaScript.
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
        """Použije úplný seznam Show All, nebo vložený seznam u krátké série."""
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
            # Krátké série mají všechna vydání přímo na stránce a tlačítko Show All chybí.
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


def file_digest(path: Path) -> str:
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def validate_image(path: Path):
    """Ověří strukturu i dekódování; samotné verify JPEG payload nekontroluje."""
    with Image.open(path) as image:
        image.verify()
    with Image.open(path) as image:
        image.load()


def image_receipt(path: Path, digest: str):
    receipt = path.with_suffix('.verified')
    temporary = receipt.with_suffix('.verified.part')
    try:
        temporary.write_text(IMAGE_CHECK_VERSION + '\n' + digest, encoding='ascii')
        temporary.replace(receipt)
    finally:
        temporary.unlink(missing_ok=True)


def valid_image(path: Path) -> bool:
    """Stará cache se dekóduje jednou, ověřená se porovná podle obsahu."""
    try:
        digest = file_digest(path)
        receipt = path.with_suffix('.verified')
        if receipt.exists() and receipt.read_text(encoding='ascii') == IMAGE_CHECK_VERSION + '\n' + digest:
            return True
        validate_image(path)
        image_receipt(path, digest)
        return True
    except (OSError, ValueError, UnicodeError):
        return False


def draw_xobject(pdf, name, width, height):
    pdf._currentPageHasImages = 1
    pdf.saveState()
    pdf.scale(width, height)
    pdf.doForm(name)
    pdf.restoreState()


def draw_jpeg(pdf, path: Path, width, height, data=None):
    """Vloží JPEG přímo jako XObject, bez rozbalení pixelů pro otisk ImageReader."""
    # drawImage(ImageReader(...)) volá getRGBData i při zachování JPEG komprese.
    # Hash komprimovaného obsahu deduplikuje stejné stránky pod různými URL.
    name = 'jpeg_' + (hashlib.sha256(data).hexdigest() if data is not None else file_digest(path))
    if pdf._doc.getXObjectName(name) not in pdf._doc.idToObject:
        image = PDFImageXObject(name)
        with (io.BytesIO(data) if data is not None else path.open('rb')) as stream:
            if not image.loadImageFromJPEG(stream):
                raise ValueError(f'Nelze precist JPEG: {path}')
        pdf._doc.addForm(name, image)
    draw_xobject(pdf, name, width, height)


def png_stream(data):
    """Původní PNG IDAT jako PDF Flate + PNG predictor, bez rozbalení pixelů.

    Pro alpha/tRNS a interlacing vrátí None; ty zpracuje ověřená cesta Pillow.
    """
    if not data.startswith(b'\x89PNG\r\n\x1a\n'):
        return None
    offset, chunks, palette, header = 8, [], None, None
    while offset + 12 <= len(data):
        length = struct.unpack_from('>I', data, offset)[0]
        kind = data[offset + 4:offset + 8]
        payload = data[offset + 8:offset + 8 + length]
        if len(payload) != length:
            return None
        if kind == b'IHDR':
            header = struct.unpack('>IIBBBBB', payload)
        elif kind == b'PLTE':
            palette = payload
        elif kind == b'tRNS':
            return None
        elif kind == b'IDAT':
            chunks.append(payload)
        elif kind == b'IEND':
            break
        offset += length + 12
    if header is None or not chunks:
        return None
    width, height, bits, color, compression, filtering, interlaced = header
    if color not in (0, 2, 3) or interlaced or compression or filtering:
        return None
    colorspace = PDFName('DeviceRGB' if color == 2 else 'DeviceGray')
    if color == 3:
        if not palette:
            return None
        colorspace = PDFArray([PDFName('Indexed'), PDFName('DeviceRGB'),
                               len(palette) // 3 - 1, PDFString(palette, enc='raw')])
    return PDFStream(PDFDictionary({
        'Type': PDFName('XObject'), 'Subtype': PDFName('Image'),
        'Width': width, 'Height': height, 'BitsPerComponent': bits,
        'ColorSpace': colorspace, 'Filter': PDFName('FlateDecode'),
        'DecodeParms': PDFDictionary({'Predictor': 15, 'Colors': 3 if color == 2 else 1,
                                      'BitsPerComponent': bits, 'Columns': width}),
    }), content=b''.join(chunks), filters=[])


def draw_lossless(pdf, source, path, width, height, jpeg_quality=None):
    data = path.read_bytes() if source.format == 'PNG' else None
    stream = png_stream(data) if data is not None else None
    if stream is None and source.mode in ('1', 'P', 'L', 'RGB') and 'transparency' not in source.info:
        buffer = io.BytesIO()
        source.save(buffer, format='PNG')
        data = buffer.getvalue()
        stream = png_stream(data)
    if stream is not None:
        # Ztrátový převod je explicitní a netýká se jednobitových/paletových stran.
        if jpeg_quality is not None and source.mode in ('L', 'RGB') and 'transparency' not in source.info:
            buffer = io.BytesIO()
            source.save(buffer, format='JPEG', quality=jpeg_quality, optimize=True)
            jpeg = buffer.getvalue()
            if len(jpeg) < len(stream.content):
                draw_jpeg(pdf, path, width, height, data=jpeg)
                return
        name = 'png_' + hashlib.sha256(data).hexdigest()
        if pdf._doc.getXObjectName(name) not in pdf._doc.idToObject:
            pdf._doc.addForm(name, stream)
        draw_xobject(pdf, name, width, height)
    else:
        # ImageReader sám neumí všechny PNG tRNS varianty (zejména L a RGB).
        if 'transparency' in source.info:
            with source.convert('RGBA') as rgba:
                pdf.drawImage(ImageReader(rgba), 0, 0, width=width, height=height, mask='auto')
        else:
            pdf.drawImage(ImageReader(source), 0, 0, width=width, height=height, mask='auto')


def make_pdf(images: list[Path], output: Path, title: str, jpeg_quality=None):
    """Vytvoří jednu PDF stránku na obrázek a hotový soubor atomicky přejmenuje."""
    temporary = output.with_suffix(".pdf.part")
    try:
        pdf = canvas.Canvas(str(temporary), pageCompression=1, pdfVersion=(1, 5))
        pdf.setTitle(series_metadata(title)[0] or title)
        metadata = PDFStream(
            PDFDictionary({'Type': PDFName('Metadata'), 'Subtype': PDFName('XML')}),
            content=pdf_xmp(title), filters=[],
        )
        # ReportLab nemá veřejný setter pro XMP; stream připojujeme do katalogu PDF.
        pdf._doc.Catalog.Metadata = metadata
        for path in images:
            with Image.open(path) as source:
                # Měníme fyzickou velikost stránky, nikoli počet pixelů obrázku.
                # Zachováme poměr stran a omezíme rozměry PDF stránky na 14 000 bodů.
                width, height = source.size
                scale = min(0.75, 14000 / max(width, height))
                w, h = width * scale, height * scale
                pdf.setPageSize((w, h))
                # JPEG i kompatibilní PNG zachovají komprimované streamy.
                # Ostatní formáty a průhlednost se zpracují po jednom obrázku.
                if source.format == 'JPEG':
                    draw_jpeg(pdf, path, w, h)
                else:
                    draw_lossless(pdf, source, path, w, h, jpeg_quality)
                pdf.showPage()
        pdf.save()
        temporary.replace(output)
    finally:
        temporary.unlink(missing_ok=True)


def finish_pdf(paths, pdf, title, manifest, signature, legacy_pdf=None, jpeg_quality=None):
    """Dokončí PDF a volitelný otisk; funkce na úrovni modulu funguje i v procesu na Windows."""
    try:
        make_pdf(paths, pdf, title, jpeg_quality)
    finally:
        # ReportLab vytváří cykly; dlouho žijící worker je nesmí držet do další dávky.
        gc.collect()
    if manifest is not None:
        temporary = manifest.with_suffix(".sha256.part")
        temporary.write_text(signature)
        temporary.replace(manifest)
    else:
        # Starý otisk nesmí potvrdit nově přestavěné PDF s jinou kvalitou.
        pdf.with_suffix('.pages.sha256').unlink(missing_ok=True)
    # Staré PDF s hashem odstraníme až po dokončení nové verze a případného otisku.
    if legacy_pdf is not None and legacy_pdf != pdf:
        legacy_pdf.unlink(missing_ok=True)
        legacy_pdf.with_suffix('.pages.sha256').unlink(missing_ok=True)
    return pdf


def download_chapter(client: Client, chapter: Chapter, output: Path, force: bool, workers: int = 4,
                     pdf_executor=None, write_manifest: bool = False, image_executor=None,
                     jpeg_quality=None, rebuild=False):
    """Stáhne obrázky vydání a vytvoří PDF, nebo vrátí Future z PDF procesu.

    Hotová PDF přeskakuje (vrací None); kontrola aktuálnosti otiskem je volitelná. Interní cache zůstává ve starém
    formátu s hashem, aby změny názvů PDF nevyvolaly nové stahování obrázků.
    """
    if workers < 1:
        raise ValueError("Pocet vlaken musi byt alespon 1")
    name = safe_name(chapter)
    pdf = output / f"{pdf_name(chapter)}.pdf"
    legacy_pdf = output / f"{name}.pdf"
    # Bez otisku má existence PDF význam „hotovo“; obnovu vyžádá --rebuild/--force.
    if pdf.exists() and not force and not rebuild and not write_manifest:
        LOG.info("Preskakuji existujici PDF: %s", pdf.name)
        return
    urls = image_urls(client.chapter_html(chapter.url), chapter.url)
    # Otisk váže PDF na verzi generátoru, metadata a aktuální pořadí obrázků.
    signature = hashlib.sha256((PDF_FORMAT_VERSION + f"\njpeg_quality={jpeg_quality}\n" + chapter.title + "\n" + "\n".join(urls)).encode()).hexdigest() if write_manifest else None
    manifest = pdf.with_suffix('.pages.sha256') if write_manifest else None
    if pdf.exists() and manifest is not None and manifest.exists() and manifest.read_text() == signature and not force and not rebuild:
        LOG.info("Preskakuji existujici PDF: %s", pdf.name)
        return
    cache = output / ".images" / name
    cache.mkdir(parents=True, exist_ok=True)
    def download_image(index, url):
        key = hashlib.sha256(url.encode()).hexdigest()[:12]
        path = cache / f"{index:05d}-{key}.img"
        if force or not valid_image(path):
            temporary = path.with_suffix(".part")
            try:
                if hasattr(client, 'download'):
                    client.download(url, temporary, chapter.url)
                else:
                    # Jednoduché alternativní klienty mohou poskytovat pouze get.
                    temporary.write_bytes(client.get(url, chapter.url))
                validate_image(temporary)
                digest = file_digest(temporary)
                temporary.replace(path)
                image_receipt(path, digest)
            finally:
                temporary.unlink(missing_ok=True)
        return path

    # Obrázky mohou dorazit různě rychle; cesty ukládáme podle původního indexu.
    paths = [None] * len(urls)
    with (nullcontext(image_executor) if image_executor is not None else
          ThreadPoolExecutor(max_workers=workers, thread_name_prefix="image")) as executor:
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
            # Zrušíme dosud nezačaté úlohy; běžící práci executor nechá doběhnout.
            for future in pending:
                future.cancel()
            # Sdílený pool nesmí začít další kapitolu, dokud dobíhá tato.
            wait(pending)
            raise
    if pdf_executor is not None:
        LOG.info("Predavam do tvorby PDF: %s", chapter.title)
        return pdf_executor.submit(finish_pdf, paths, pdf, chapter.title, manifest, signature, legacy_pdf, jpeg_quality)
    finish_pdf(paths, pdf, chapter.title, manifest, signature, legacy_pdf, jpeg_quality)
    LOG.info("Hotovo: %s", pdf)


def run_downloads(client, chapters, output, force, workers, pdf_workers, write_manifest=False,
                  pdf_tasks_per_child=10, jpeg_quality=None, rebuild=False):
    """Propojí stahování s omezenou frontou PDF procesů a sečte chyby obou fází."""
    # Kontrola před spuštěním brání přepsání PDF dvěma různými vydáními.
    names = {}
    for chapter in chapters:
        name = pdf_name(chapter).casefold()
        if name in names and names[name] != chapter.url:
            raise ValueError(f'Dve kapitoly maji stejny vystupni nazev: {pdf_name(chapter)}.pdf')
        names[name] = chapter.url
    failures = 0
    pending = {}

    def collect(block=False):
        """Převezme výsledky PDF; volitelně počká na první dokončený proces."""
        nonlocal failures
        done = wait(pending, return_when=FIRST_COMPLETED).done if block else {f for f in pending if f.done()}
        for future in done:
            title = pending.pop(future)
            try:
                LOG.info("Hotovo: %s", future.result())
            except Exception as exc:
                failures += 1
                LOG.error("Tvorba PDF %s selhala: %s", title, exc)

    # Recyklace po dávce omezuje režii startu i dlouhodobé držení paměti.
    with ProcessPoolExecutor(max_workers=pdf_workers, max_tasks_per_child=pdf_tasks_per_child) as executor, \
            ThreadPoolExecutor(max_workers=workers, thread_name_prefix='image') as image_executor:
        try:
            for chapter in chapters:
                collect()
                # Fronta smí obsahovat nejvýše dvojnásobek počtu PDF procesů.
                # Předáváme pouze cesty; velké obrazové bajty se mezi procesy nekopírují.
                while len(pending) >= pdf_workers * 2:
                    LOG.info("Cekam na uvolneni fronty PDF...")
                    collect(block=True)
                try:
                    future = download_chapter(client, chapter, output, force, workers, executor, write_manifest,
                                              image_executor, jpeg_quality, rebuild)
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
            # Zrušíme dosud nezačaté úlohy; běžící práci executor nechá doběhnout.
            for future in pending:
                future.cancel()
            raise
    return failures


def main(argv=None) -> int:
    """Zpracuje parametry, vybere zdroj a vrátí kód 0 (úspěch), 1 nebo 130 (Ctrl+C)."""
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
    parser.add_argument("--rebuild", action="store_true", help="Znovu vytvorit PDF z cache bez nuceneho stahovani obrazku")
    parser.add_argument("--jpeg-quality", type=int, help="Volitelna ztratova komprese ne-JPEG RGB/gray obrazku (1-95); puvodni JPEG se nemeni")
    parser.add_argument("--write-manifest", action="store_true", help="Ukladat a kontrolovat otisk .pages.sha256 vedle PDF (vychozi vypnuto)")
    parser.add_argument("--timeout", type=float, default=60, help="Limit jednoho pokusu o prenos v sekundach (vychozi 60)")
    parser.add_argument("--workers", type=int, default=4, help="Pocet soubezne stahovanych obrazku (vychozi 4, 1 = postupne)")
    parser.add_argument("--pdf-workers", type=int, default=1, help="Pocet procesu pro PDF bezici soubezne se stahovanim (vychozi 1)")
    parser.add_argument("--pdf-tasks-per-child", type=int, default=10, help="Pocet PDF pred restartem procesu (vychozi 10; 1 pro nizsi spotrebu pameti)")
    parser.add_argument("--delay", type=float, default=0.5, help="Prodleva pred pozadavkem v kazdem vlakne v sekundach")
    args = parser.parse_args(argv)
    if bool(args.feed) == bool(args.chapter):
        parser.error("Zadejte RSS / stranku serie, nebo --chapter URL")
    if args.oldest_first and args.newest_first:
        parser.error("Vyberte pouze jeden smer razeni")
    if not math.isfinite(args.timeout) or not math.isfinite(args.delay) or args.timeout <= 0 or args.delay < 0 or args.workers < 1 or args.pdf_workers < 1 or args.pdf_tasks_per_child < 1 or (args.limit is not None and args.limit <= 0):
        parser.error("timeout, limit, workers a pdf-workers musi byt kladne, delay nezaporne")
    if args.jpeg_quality is not None and not 1 <= args.jpeg_quality <= 95:
        parser.error("jpeg-quality musi byt v rozsahu 1 az 95")
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s", datefmt="%H:%M:%S")
    client = None
    try:
        client = Client(args.timeout, args.delay, args.user_agent)
        if args.chapter:
            url = http_url(args.chapter)
            page = SeriesPage()
            html = client.get(url).decode('utf-8-sig')
            client.prefetched_html[url] = html
            page.feed(html)
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
        # Limit aplikujeme až po seřazení, aby --limit 1 vybral první vydání.
        chapters = chapters[:args.limit]
        if args.list:
            for chapter in chapters:
                print(f"{chapter.title}\t{chapter.url}")
            return 0
        args.output.mkdir(parents=True, exist_ok=True)
        LOG.info("Stahovani pomoci %d vlaken.", args.workers)
        failures = run_downloads(client, chapters, args.output, args.force, args.workers, args.pdf_workers,
                                 args.write_manifest, args.pdf_tasks_per_child, args.jpeg_quality, args.rebuild)
        return 1 if failures else 0
    except KeyboardInterrupt:
        LOG.error("Preruseno. Dalsi spusteni navaze na stazene obrazky.")
        return 130
    except Exception as exc:
        LOG.error("Chyba: %s", exc)
        if isinstance(exc, urllib.error.HTTPError) and exc.code == 403:
            LOG.error("Server odmitl HTTP pozadavek (403). Zmena klienta na curl/wget sama o sobe nezarucuje pristup.")
        return 1
    finally:
        if client is not None:
            client.close()


# Ochrana je nutná i pro Windows: PDF proces při importu nesmí spustit celý program.
if __name__ == "__main__":
    multiprocessing.freeze_support()
    sys.exit(main())
