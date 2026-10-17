import tempfile
import hashlib
import io
import threading
import time
import unittest
import urllib.error
import xml.etree.ElementTree as ET
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from unittest.mock import patch, MagicMock
from PIL import Image
from pypdf import PdfReader

from manga_leech import Chapter, Client, SeriesPage, chapter_sort_key, download_chapter, finish_pdf, image_urls, make_pdf, parse_feed, run_downloads, pdf_name, safe_name


class Tests(unittest.TestCase):
    def test_page_labels_in_series_rss_and_names(self):
        page = SeriesPage()
        page.feed('<a href="/chapters/ten"><span>Page. 10</span><time>2026</time></a><a href="/chapters/one">Page. 1</a><a href="/chapters/half">Page 1.5</a>')
        chapters = [Chapter('Horimiya ' + c.title, c.url) for c in page.chapters]
        self.assertEqual([pdf_name(c) for c in sorted(chapters, key=chapter_sort_key)], ['Horimiya - Ch 001', 'Horimiya - Ch 001.5', 'Horimiya - Ch 010'])
        rss = b'<rss><channel><item><title>Horimiya Page. 25</title><link>/chapters/25</link></item></channel></rss>'
        self.assertEqual(pdf_name(parse_feed(rss, 'https://example.org')[0]), 'Horimiya - Ch 025')

    def test_page_pdf_title_and_index(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            image = root / 'page.png'
            Image.new('RGB', (20, 40), 'red').save(image)
            output = root / 'Horimiya - Ch 001.pdf'
            make_pdf([image], output, 'Horimiya Page. 1')
            reader = PdfReader(output)
            self.assertEqual(reader.metadata.title, 'Horimiya')
            xmp = ET.fromstring(reader.trailer['/Root']['/Metadata'].get_data())
            self.assertEqual(xmp.find('.//{http://calibre-ebook.com/xmp-namespace}series').text, 'Horimiya')
            self.assertEqual(xmp.find('.//{http://calibre-ebook.com/xmp-namespace-series-index}series_index').text, '1')

    def test_pdf_names(self):
        for number, expected in [('0', '000'), ('25', '025'), ('104.1', '104.1'), ('1000', '1000')]:
            self.assertEqual(pdf_name(Chapter(f'Overgeared Chapter {number}', 'https://example.org/c')), f'Overgeared - Ch {expected}')

    def test_legacy_pdf_migration_uses_existing_cache(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            chapter = Chapter('Overgeared Chapter 25', 'https://example.org/c')
            image_url = 'https://example.org/page.png'
            cache = root / '.images' / safe_name(chapter)
            cache.mkdir(parents=True)
            Image.new('RGB', (20, 40), 'red').save(cache / f'00001-{hashlib.sha256(image_url.encode()).hexdigest()[:12]}.img', format='PNG')
            old = root / f'{safe_name(chapter)}.pdf'
            old.write_bytes(b'legacy')
            class CachedClient:
                def chapter_html(self, url):
                    return '<section id="chapter-images"><img src="/page.png" /></section>'
                def get(self, *args):
                    raise AssertionError('Must use image cache')
            download_chapter(CachedClient(), chapter, root, False)
            self.assertFalse(old.exists())
            pdf = root / 'Overgeared - Ch 025.pdf'
            self.assertEqual(PdfReader(pdf).metadata.title, 'Overgeared')

    def test_kavita_xmp_groups_chapters_and_keeps_decimal_index(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            image = root / 'page.img'
            Image.new('RGB', (20, 40), 'red').save(image, format='JPEG')
            for number in ['1', '104.1']:
                title = f'Čaroděj & <Dragon> Chapter {number}'
                pdf = root / f'{number}.pdf'
                make_pdf([image], pdf, title)
                reader = PdfReader(pdf)
                self.assertEqual(reader.metadata.title, 'Čaroděj & <Dragon>')
                stream = reader.trailer['/Root']['/Metadata'].get_object()
                self.assertEqual(stream['/Subtype'], '/XML')
                xmp = ET.fromstring(stream.get_data())
                self.assertEqual(xmp.find('.//{http://calibre-ebook.com/xmp-namespace}series').text, 'Čaroděj & <Dragon>')
                self.assertEqual(xmp.find('.//{http://calibre-ebook.com/xmp-namespace-series-index}series_index').text, number)
                self.assertEqual(xmp.find('.//{http://purl.org/dc/elements/1.1/}title/{http://www.w3.org/1999/02/22-rdf-syntax-ns#}Alt/{http://www.w3.org/1999/02/22-rdf-syntax-ns#}li').text, 'Čaroděj & <Dragon>')
                embedded = next(iter(reader.pages[0]['/Resources']['/XObject'].values())).get_object()
                self.assertEqual(embedded.get_data(), image.read_bytes())

    def test_slow_trickle_has_total_deadline_and_retries(self):
        client = Client(1, 0)
        response = MagicMock()
        response.__enter__.return_value = response
        response.read1.return_value = b'x'
        opener = MagicMock()
        opener.open.return_value = response
        ticks = iter(i * .6 for i in range(100))
        with patch.object(client, 'thread_opener', return_value=opener), patch('manga_leech.time.monotonic', side_effect=lambda: next(ticks)), patch('manga_leech.time.sleep'), self.assertLogs('manga-leech', level='WARNING') as logs:
            with self.assertRaises(TimeoutError):
                client.get('https://example.org/slow')
        self.assertEqual(opener.open.call_count, 3)
        self.assertEqual(response.__exit__.call_count, 3)
        self.assertTrue(any('pokus 3/3' in line for line in logs.output))

    def test_rate_limit_retry_after_and_recovery(self):
        client = Client(60, 0)
        response = MagicMock()
        response.__enter__.return_value = response
        response.read1.side_effect = [b'abc', b'']
        response.headers = {'Content-Length': '3'}
        opener = MagicMock()
        opener.open.side_effect = [urllib.error.HTTPError('https://example.org/image', 429, 'Rate limit', {'Retry-After': '0'}, None), response]
        with patch.object(client, 'thread_opener', return_value=opener), patch('manga_leech.time.sleep'), self.assertLogs('manga-leech', level='WARNING') as logs:
            self.assertEqual(client.get('https://example.org/image'), b'abc')
        self.assertIn('example.org', client.cooldowns)
        self.assertTrue(any('HTTP 429' in line for line in logs.output))
        self.assertEqual(client.retry_delay({'Retry-After': '25'}, 2), 25)
        self.assertEqual(client.retry_delay({'Retry-After': 'invalid'}, 2), 2)

    def test_truncated_body_is_not_accepted(self):
        client = Client(60, 0)
        response = MagicMock()
        response.__enter__.return_value = response
        response.read1.side_effect = [b'a', b'', b'a', b'', b'a', b'']
        response.headers = {'Content-Length': '100'}
        opener = MagicMock()
        opener.open.return_value = response
        with patch.object(client, 'thread_opener', return_value=opener), patch('manga_leech.time.sleep'), self.assertLogs('manga-leech', level='WARNING'):
            with self.assertRaises(Exception):
                client.get('https://example.org/truncated')
        self.assertEqual(opener.open.call_count, 3)

    def test_jpeg_bytes_preserved_even_with_img_extension(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            paths = []
            for mode, color in [('RGB', 'red'), ('L', 128), ('CMYK', (0, 30, 80, 0))]:
                path = root / f'{mode}.img'
                Image.new(mode, (40, 80), color).save(path, format='JPEG')
                paths.append(path)
            pdf = root / 'original.pdf'
            make_pdf(paths, pdf, 'JPEG')
            reader = PdfReader(pdf)
            self.assertEqual(len(reader.pages), 3)
            for page, source in zip(reader.pages, paths):
                image = next(iter(page['/Resources']['/XObject'].values())).get_object()
                self.assertEqual(list(image['/Filter']), ['/DCTDecode'])
                self.assertEqual(image.get_data(), source.read_bytes())

    def test_pdf_pipeline_overlaps_next_download_and_waits(self):
        started = threading.Event()
        next_download = threading.Event()
        data = io.BytesIO()
        Image.new('RGB', (20, 40), 'red').save(data, format='PNG')
        class FakeClient:
            def chapter_html(self, url):
                if url.endswith('/2'):
                    if not started.wait(5):
                        raise AssertionError('PDF did not start')
                    next_download.set()
                return '<section id="chapter-images"><img src="/page.png" /></section>'
            def get(self, url, referer=None):
                return data.getvalue()
        def render(*args):
            if args[2] == 'Chapter 1':
                started.set()
                if not next_download.wait(5):
                    raise AssertionError('Next download blocked on PDF')
            return finish_pdf(*args)
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            chapters = [Chapter(f'Chapter {i}', f'https://example.org/{i}') for i in [1, 2]]
            with patch('manga_leech.ProcessPoolExecutor', ThreadPoolExecutor), patch('manga_leech.finish_pdf', side_effect=render):
                self.assertEqual(run_downloads(FakeClient(), chapters, root, False, 2, 1), 0)
            self.assertEqual(len(list(root.glob('*.pdf'))), 2)
            self.assertEqual(len(list(root.glob('*.pages.sha256'))), 2)

    def test_real_pdf_process_and_failure_result(self):
        data = io.BytesIO()
        Image.new('RGB', (20, 40), 'red').save(data, format='PNG')
        class FakeClient:
            def chapter_html(self, url):
                return '<section id="chapter-images"><img src="/page.png" /></section>'
            def get(self, url, referer=None):
                return data.getvalue()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            chapters = [Chapter('Chapter 1', 'https://example.org/1')]
            self.assertEqual(run_downloads(FakeClient(), chapters, root, False, 2, 1), 0)
            self.assertEqual(len(PdfReader(next(root.glob('*.pdf'))).pages), 1)
            with patch('manga_leech.ProcessPoolExecutor', ThreadPoolExecutor), patch('manga_leech.finish_pdf', side_effect=OSError('PDF failure')):
                self.assertEqual(run_downloads(FakeClient(), chapters, root, True, 2, 1), 1)

    def test_concurrent_download_keeps_pdf_order(self):
        barrier = threading.Barrier(3)
        class ParallelClient:
            def chapter_html(self, url):
                return '<section id="chapter-images">' + ''.join(f'<img src="/{i}.png" />' for i in range(1, 4)) + '</section>'
            def get(self, url, referer=None):
                index = int(url.rsplit('/', 1)[-1].split('.')[0])
                barrier.wait(timeout=5)  # Fails if downloads are actually sequential.
                time.sleep((3 - index) * .02)
                data = io.BytesIO()
                Image.new('RGB', (100, 100 * index), 'red').save(data, format='PNG')
                return data.getvalue()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            download_chapter(ParallelClient(), Chapter('Parallel', 'https://example.org/c'), root, False, workers=3)
            pages = PdfReader(next(root.glob('*.pdf'))).pages
            self.assertEqual([float(p.mediabox.height) / float(p.mediabox.width) for p in pages], [1, 2, 3])

    def test_http_openers_are_per_thread(self):
        client = Client(60, 0)
        barrier = threading.Barrier(3)
        def opener():
            first = client.thread_opener()
            barrier.wait(timeout=5)
            self.assertIs(first, client.thread_opener())
            return first
        with ThreadPoolExecutor(max_workers=3) as executor:
            openers = list(executor.map(lambda _: opener(), range(3)))
        self.assertEqual(len({id(o) for o in openers}), 3)

    def test_full_series_not_preview_and_dedup(self):
        class SeriesClient(Client):
            def __init__(self):
                self.calls = []
            def get(self, url, referer=None):
                self.calls.append(url)
                if len(self.calls) == 1:
                    return b'<title>My Manga | Weeb Central</title><a href="/chapters/preview">Chapter 999</a><button hx-get="/series/abc/full-chapter-list">All</button>'
                return b'<a href="/chapters/ten"><span>Chapter 10</span><time>2026</time></a><a href="/chapters/one"><img /><span>Chapter 1</span>Last Read</a><a href="/chapters/one">Chapter 1</a><a href="/series/ad">Chapter 7</a>'
        client = SeriesClient()
        chapters = sorted(client.series_chapters('https://example.org/series/abc/name'), key=chapter_sort_key)
        self.assertEqual([c.title for c in chapters], ['My Manga Chapter 1', 'My Manga Chapter 10'])
        self.assertEqual(chapters[0].url, 'https://example.org/chapters/one')
        self.assertEqual(client.calls[1], 'https://example.org/series/abc/full-chapter-list')

    def test_series_missing_full_list_fails(self):
        class MissingClient(Client):
            def __init__(self):
                pass
            def get(self, url, referer=None):
                return b'<a href="/chapters/preview">Chapter 999</a>'
        with self.assertRaises(ValueError):
            MissingClient().series_chapters('https://example.org/series/abc')

    def test_self_closing_images_do_not_close_reader(self):
        html = '<img src="ad.png" /><section id="chapter-images"><img src="/1.png" /><img src="/2.png" /><div><img src="/3.png" /></div></section><img src="logo.png" />'
        self.assertEqual(image_urls(html, 'https://example.org'), ['https://example.org/1.png', 'https://example.org/2.png', 'https://example.org/3.png'])

    def test_numeric_chapter_order(self):
        chapters = [Chapter('Manga Chapter ' + number, 'https://example.org/' + number) for number in ['10', '2', '1', '2.5']]
        self.assertEqual([c.title for c in sorted(chapters, key=chapter_sort_key)], ['Manga Chapter 1', 'Manga Chapter 2', 'Manga Chapter 2.5', 'Manga Chapter 10'])

    def test_reader_fragment_without_browser(self):
        class FragmentClient(Client):
            def __init__(self):
                self.calls = []
            def get(self, url, referer=None):
                self.calls.append((url, referer))
                if len(self.calls) == 1:
                    return b'<section id="chapter-images"></section><script>htmx.ajax("GET", "https://example.org/chapters/1/images?is_prev=False")</script>'
                return b'<section id="chapter-images"><img src="/page.png"></section>'
        client = FragmentClient()
        url = 'https://example.org/chapters/1'
        self.assertEqual(image_urls(client.chapter_html(url), url), ['https://example.org/page.png'])
        self.assertEqual(client.calls[1], (url + '/images?is_prev=False&reading_style=long_strip&current_page=1', url))

    def test_feed_dedup_and_atom(self):
        rss = b'<rss><channel><item><title>A</title><link>/chapters/1</link></item><item><link>/chapters/1</link></item></channel></rss>'
        self.assertEqual(parse_feed(rss, 'https://example.org/rss'), [Chapter('A', 'https://example.org/chapters/1')])
        atom = b'<feed xmlns="http://www.w3.org/2005/Atom"><entry><title>B</title><link href="/2"/></entry></feed>'
        self.assertEqual(parse_feed(atom, 'https://example.org/rss')[0].url, 'https://example.org/2')

    def test_only_reader_images_in_order(self):
        html = '<img src="ad.jpg"><section id="chapter-images"><div><img src="/2.png"><img data-src="/1.png" src="placeholder"></div><img src="/2.png"></section><img src="logo.png">'
        self.assertEqual(image_urls(html, 'https://example.org/c'), ['https://example.org/2.png', 'https://example.org/1.png'])
        with self.assertRaises(ValueError):
            image_urls('<img src="ad.jpg">', 'https://example.org')

    def test_pdf_dimensions_and_resume(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            image = root / 'test.png'
            Image.new('RGB', (100, 300), 'red').save(image)
            class FakeClient:
                calls = 0
                def chapter_html(self, url):
                    return '<section id="chapter-images"><img src="/1.png"><img src="/2.png"></section>'
                def get(self, url, referer=None):
                    self.calls += 1
                    return image.read_bytes()
            client = FakeClient()
            chapter = Chapter('Test', 'https://example.org/chapter')
            download_chapter(client, chapter, root, False)
            pdf = next(root.glob('*.pdf'))
            reader = PdfReader(pdf)
            self.assertEqual(len(reader.pages), 2)
            self.assertAlmostEqual(float(reader.pages[0].mediabox.height) / float(reader.pages[0].mediabox.width), 3)
            download_chapter(client, chapter, root, False)
            self.assertEqual(client.calls, 2)
            # Previous renderer signatures must rebuild using cached image files.
            manifest = next(root.glob('*.pages.sha256'))
            manifest.write_text(hashlib.sha256(b'https://example.org/1.png\nhttps://example.org/2.png').hexdigest())
            pdf.write_bytes(b'old oversized PDF')
            download_chapter(client, chapter, root, False)
            self.assertEqual(client.calls, 2)
            self.assertEqual(len(PdfReader(pdf).pages), 2)
            # Old releases wrote incomplete PDFs without a page-list manifest.
            next(root.glob('*.pages.sha256')).unlink()
            pdf.write_bytes(b'old incomplete PDF')
            download_chapter(client, chapter, root, False)
            self.assertEqual(len(PdfReader(pdf).pages), 2)
            pdf.unlink()
            download_chapter(client, chapter, root, False)
            self.assertEqual(client.calls, 2)

    def test_failed_image_never_publishes_pdf(self):
        with tempfile.TemporaryDirectory() as directory:
            class BrokenClient:
                def chapter_html(self, url):
                    return '<section id="chapter-images"><img src="/1.png"></section>'
                def get(self, url, referer=None):
                    return b'<html>error</html>'
            with self.assertRaisesRegex(RuntimeError, 'Obrazek 1/1'):
                download_chapter(BrokenClient(), Chapter('Broken', 'https://example.org/c'), Path(directory), False)
            self.assertEqual(list(Path(directory).glob('*.pdf')), [])


if __name__ == '__main__':
    unittest.main()
