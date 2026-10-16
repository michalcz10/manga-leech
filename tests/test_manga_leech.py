import tempfile
import io
import threading
import time
import unittest
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from PIL import Image
from pypdf import PdfReader

from manga_leech import Chapter, Client, chapter_sort_key, download_chapter, image_urls, make_pdf, parse_feed


class Tests(unittest.TestCase):
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
            with self.assertRaises(OSError):
                download_chapter(BrokenClient(), Chapter('Broken', 'https://example.org/c'), Path(directory), False)
            self.assertEqual(list(Path(directory).glob('*.pdf')), [])


if __name__ == '__main__':
    unittest.main()
