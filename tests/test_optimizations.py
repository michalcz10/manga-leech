import io
import struct
import tempfile
import threading
import unittest
import urllib.error
from collections import Counter
from concurrent.futures import ThreadPoolExecutor
from contextlib import contextmanager
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from unittest.mock import MagicMock, patch

from PIL import Image, ImageDraw
from pypdf import PdfReader
import pypdfium2 as pdfium

from python.manga_leech import (Chapter, Client, download_chapter, finish_pdf, main,
                         make_pdf, run_downloads, valid_image)


def image_bytes(fmt='JPEG', mode='RGB'):
    buffer = io.BytesIO()
    image = Image.new(mode, (128, 192), 'white')
    ImageDraw.Draw(image).rectangle((5, 10, 100, 180), fill='black')
    image.save(buffer, format=fmt)
    return buffer.getvalue()


def embedded(page):
    return next(iter(page['/Resources']['/XObject'].values())).get_object()


@contextmanager
def local_server():
    records = []
    jpeg = image_bytes()

    class Handler(BaseHTTPRequestHandler):
        protocol_version = 'HTTP/1.1'

        def log_message(self, *args):
            pass

        def do_GET(self):
            records.append((self.path, self.client_address[1], dict(self.headers)))
            if self.path == '/redirect':
                self.send_response(302)
                self.send_header('Location', '/cookie')
                self.send_header('Set-Cookie', 'session=ok; Path=/')
                self.send_header('Content-Length', '0')
                self.end_headers()
                return
            if self.path == '/cookie':
                body = self.headers.get('Cookie', '').encode()
            elif self.path.startswith('/chapter/'):
                body = b'<title>Chapter 1 | Test | Weeb Central</title><section id="chapter-images"><img src="/image"></section>'
            else:
                body = jpeg
            self.send_response(200)
            if self.path == '/truncated':
                self.send_header('Content-Length', str(len(body) + 10))
                self.send_header('Connection', 'close')
                self.close_connection = True
            else:
                self.send_header('Content-Length', str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            self.wfile.flush()

    server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    client = Client(5, 0)
    try:
        with patch.dict('os.environ', {'NO_PROXY': '127.0.0.1', 'no_proxy': '127.0.0.1'}):
            yield client, f'http://127.0.0.1:{server.server_port}', records, jpeg
    finally:
        client.close()
        server.shutdown()
        server.server_close()
        thread.join(timeout=5)


class OptimizationTests(unittest.TestCase):
    def test_corrupt_jpeg_is_rejected_without_publishing_pdf(self):
        data = image_bytes()
        client = MagicMock(spec=['chapter_html', 'get'])
        client.chapter_html.return_value = '<section id="chapter-images"><img src="/image"></section>'
        client.get.return_value = data[:len(data) // 2]
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with self.assertRaises(RuntimeError):
                download_chapter(client, Chapter('Test Chapter 1', 'https://example.org/c'), root, False)
            self.assertFalse(list(root.glob('*.pdf')))
            self.assertFalse(list(root.rglob('*.part')))
            self.assertFalse(list(root.rglob('*.img')))

    def test_verified_cache_skips_decode_but_detects_changed_bytes(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'page.img'
            data = image_bytes()
            path.write_bytes(data)
            self.assertTrue(valid_image(path))
            with patch('python.manga_leech.validate_image', side_effect=AssertionError('Repeated decode')):
                self.assertTrue(valid_image(path))
            # The file has changed even when its length stays unchanged.
            path.write_bytes(data[:len(data) // 2] + b'\0' * (len(data) - len(data) // 2))
            self.assertFalse(valid_image(path))

    def test_connections_cookies_redirects_and_streamed_download(self):
        with local_server() as (client, base, records, jpeg), tempfile.TemporaryDirectory() as directory:
            self.assertEqual(client.get(base + '/redirect'), b'session=ok')
            target = Path(directory) / 'download.part'
            client.download(base + '/image', target, base + '/chapter/1')
            self.assertEqual(target.read_bytes(), jpeg)
            self.assertEqual(len({r[1] for r in records}), 1)
            self.assertEqual(records[-1][2]['Referer'], base + '/chapter/1')
            with ThreadPoolExecutor(max_workers=1) as pool:
                self.assertEqual(pool.submit(client.get, base + '/cookie').result(), b'session=ok')

    def test_http_truncation_is_retried_and_never_published(self):
        with local_server() as (client, base, records, jpeg), tempfile.TemporaryDirectory() as directory:
            client.prefetched_html[base + '/chapter/1'] = '<section id="chapter-images"><img src="/truncated"></section>'
            with patch('python.manga_leech.time.sleep'), self.assertLogs('manga-leech', level='WARNING'):
                with self.assertRaises(RuntimeError):
                    download_chapter(client, Chapter('Test Chapter 1', base + '/chapter/1'), Path(directory), False)
            self.assertEqual(sum(r[0] == '/truncated' for r in records), 3)
            self.assertFalse(list(Path(directory).rglob('*.part')))
            self.assertFalse(list(Path(directory).glob('*.pdf')))

    def test_download_threads_reuse_connection_between_chapters(self):
        with local_server() as (client, base, records, jpeg), tempfile.TemporaryDirectory() as directory:
            chapters = [Chapter(f'Test Chapter {i}', base + f'/chapter/{i}') for i in (1, 2)]
            with patch('python.manga_leech.ProcessPoolExecutor', lambda max_workers, **kw: ThreadPoolExecutor(max_workers=max_workers)):
                self.assertEqual(run_downloads(client, chapters, Path(directory), False, 1, 1), 0)
            self.assertEqual(len({r[1] for r in records if r[0] == '/image'}), 1)

    def test_direct_chapter_uses_initial_html_once_and_closes_client(self):
        with local_server() as (client, base, records, jpeg), tempfile.TemporaryDirectory() as directory:
            with patch('python.manga_leech.Client', return_value=client), \
                    patch('python.manga_leech.ProcessPoolExecutor', lambda max_workers, **kw: ThreadPoolExecutor(max_workers=max_workers)), \
                    patch.object(client, 'close', wraps=client.close) as close, \
                    patch('python.manga_leech.logging.basicConfig'):
                self.assertEqual(main(['--chapter', base + '/chapter/1', '-o', directory]), 0)
                close.assert_called_once()
            self.assertEqual(Counter(r[0] for r in records)['/chapter/1'], 1)

    def test_retry_wait_is_bounded_for_all_retryable_statuses(self):
        client = Client(60, 0)
        for status in (429, 500, 502, 503, 504):
            with self.subTest(status=status):
                opener = MagicMock()
                opener.open.side_effect = urllib.error.HTTPError('https://example.org', status, 'error', {'Retry-After': '86400'}, None)
                with patch.object(client, 'thread_opener', return_value=opener), patch('python.manga_leech.time.sleep') as sleep, self.assertLogs('manga-leech', level='WARNING'):
                    with self.assertRaises(urllib.error.HTTPError):
                        client.get('https://example.org')
                self.assertEqual(opener.open.call_count, 1)
                self.assertTrue(all(call.args[0] <= 120 for call in sleep.call_args_list))
        for value in ('inf', '-inf', 'nan', '1e999', 'bad'):
            self.assertEqual(client.retry_delay({'Retry-After': value}, 2), 2)

    def test_cooldown_is_checked_after_per_thread_delay(self):
        client = Client(60, 0.5)
        order = []
        with patch('python.manga_leech.time.sleep', side_effect=lambda n: order.append('delay')), \
                patch.object(client, 'wait_for_host', side_effect=lambda host: order.append('cooldown')), \
                patch.object(client, 'thread_opener', side_effect=ValueError('stop')):
            with self.assertRaises(ValueError):
                client.get('https://example.org')
        self.assertEqual(order, ['delay', 'cooldown'])

    def test_stream_retry_restarts_output_instead_of_appending(self):
        client = Client(60, 0)
        first, second = MagicMock(), MagicMock()
        for response in (first, second):
            response.__enter__.return_value = response
        first.read1.side_effect = [b'bad partial response', OSError('lost connection')]
        second.read1.side_effect = [b'correct', b'']
        second.headers = {'Content-Length': '7'}
        opener = MagicMock()
        opener.open.side_effect = [first, second]
        with tempfile.TemporaryDirectory() as directory, patch.object(client, 'thread_opener', return_value=opener), patch('python.manga_leech.time.sleep'), self.assertLogs('manga-leech', level='WARNING'):
            target = Path(directory) / 'image.part'
            client.download('https://example.org', target)
            self.assertEqual(target.read_bytes(), b'correct')

    def test_png_modes_render_without_changing_pixels(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for mode in ('1', 'P', 'L', 'RGB'):
                with self.subTest(mode=mode):
                    image = Image.new(mode, (37, 53))
                    if mode == 'P':
                        image.putpalette([255, 0, 0, 0, 255, 0] + [0] * 762)
                    for y in range(image.height):
                        for x in range(image.width):
                            image.putpixel((x, y), ((x + y) % 2) * 255 if mode == '1' else
                                           (x + y) % 2 if mode == 'P' else
                                           (x * 5 + y) % 256 if mode == 'L' else (x * 5, y * 3, 50))
                    path = root / (mode + '.png')
                    image.save(path)
                    output = path.with_suffix('.pdf')
                    make_pdf([path], output, 'Test')
                    obj = embedded(PdfReader(output).pages[0])
                    self.assertEqual(obj['/BitsPerComponent'], 1 if mode == '1' else 8)
                    if mode == 'P':
                        self.assertEqual(obj['/ColorSpace'][0], '/Indexed')
                    # Independent PDF renderer also checks bit-packed PNG predictors.
                    with pdfium.PdfDocument(output) as document:
                        page = document[0]
                        bitmap = page.render(scale=4 / 3, no_smoothimage=True)
                        rendered = bitmap.to_pil().convert('RGB')
                        self.assertEqual(rendered.tobytes(), Image.open(path).convert('RGB').tobytes())
                        bitmap.close()
                        page.close()

    def test_low_bit_palette_and_16_bit_gray_preserve_samples(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for bits in (1, 2, 4):
                with self.subTest(bits=bits):
                    image = Image.new('P', (37, 53))
                    image.putpalette([component for i in range(256) for component in (i, 255 - i, i // 2)])
                    image.putdata([i % (2 ** bits) for i in range(37 * 53)])
                    path = root / f'palette-{bits}.png'
                    image.save(path, bits=bits)
                    output = path.with_suffix('.pdf')
                    make_pdf([path], output, 'Test')
                    self.assertEqual(embedded(PdfReader(output).pages[0])['/BitsPerComponent'], bits)
                    with pdfium.PdfDocument(output) as document:
                        page = document[0]
                        bitmap = page.render(scale=4 / 3, no_smoothimage=True)
                        self.assertEqual(bitmap.to_pil().convert('RGB').tobytes(), image.convert('RGB').tobytes())
                        bitmap.close()
                        page.close()
            values = [(i * 31) % 65536 for i in range(37 * 53)]
            image = Image.frombytes('I;16', (37, 53), struct.pack('<' + 'H' * len(values), *values))
            path = root / 'gray16.png'
            image.save(path)
            output = path.with_suffix('.pdf')
            make_pdf([path], output, 'Test')
            obj = embedded(PdfReader(output).pages[0])
            self.assertEqual(obj['/BitsPerComponent'], 16)
            self.assertEqual(obj.get_data(), struct.pack('>' + 'H' * len(values), *values))

    def test_palette_and_gray_transparency_preserved(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for mode in ('P', 'L', 'RGB', 'LA', 'RGBA'):
                with self.subTest(mode=mode):
                    image = Image.new(mode, (12, 20), 0)
                    if mode == 'P':
                        image.putpalette([255, 0, 0] + [0] * 765)
                        image.info['transparency'] = bytes([128] + [255] * 255)
                    elif mode in ('L', 'RGB'):
                        image.info['transparency'] = 0 if mode == 'L' else (0, 0, 0)
                    path = root / (mode + '.png')
                    image.save(path)
                    output = path.with_suffix('.pdf')
                    make_pdf([path], output, 'Test')
                    restored = PdfReader(output).pages[0].images[0].image.convert('RGBA')
                    self.assertEqual(restored.tobytes(), Image.open(path).convert('RGBA').tobytes())

    def test_jpeg_duplicates_share_one_stream(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            first, second = root / 'first.img', root / 'second.img'
            first.write_bytes(image_bytes())
            second.write_bytes(first.read_bytes())
            output = root / 'test.pdf'
            make_pdf([first, second], output, 'Test')
            pages = PdfReader(output).pages
            self.assertEqual(len(pages), 2)
            refs = [next(iter(page['/Resources']['/XObject'].values())) for page in pages]
            self.assertEqual(refs[0].idnum, refs[1].idnum)
            self.assertEqual(embedded(pages[0]).get_data(), first.read_bytes())

    def test_optional_jpeg_quality_reduces_webp_without_touching_original_jpeg(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            # Deterministic detailed input: PNG/Flate is larger than lossy JPEG.
            image = Image.new('RGB', (256, 256))
            image.putdata([((x * 13 + y * 7) % 256, (x * 5 + y * 17) % 256, (x * 19 + y * 3) % 256)
                           for y in range(256) for x in range(256)])
            webp, jpeg = root / 'page.webp', root / 'original.jpg'
            image.save(webp, quality=90)
            image.save(jpeg, quality=90)
            original = webp.read_bytes()
            lossless, compact = root / 'lossless.pdf', root / 'compact.pdf'
            make_pdf([webp, jpeg], lossless, 'Test')
            make_pdf([webp, jpeg], compact, 'Test', jpeg_quality=75)
            self.assertLess(compact.stat().st_size, lossless.stat().st_size)
            pages = PdfReader(compact).pages
            self.assertEqual(list(embedded(pages[0])['/Filter']), ['/DCTDecode'])
            self.assertEqual(embedded(pages[1]).get_data(), jpeg.read_bytes())
            self.assertEqual(webp.read_bytes(), original)
            restored = PdfReader(lossless).pages[0].images[0].image.convert('RGB')
            self.assertEqual(restored.tobytes(), Image.open(webp).convert('RGB').tobytes())

    def test_rebuild_uses_cache_and_invalidates_old_manifest(self):
        client = MagicMock(spec=['chapter_html', 'get'])
        client.chapter_html.return_value = '<section id="chapter-images"><img src="/image"></section>'
        client.get.return_value = image_bytes()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            chapter = Chapter('Test Chapter 1', 'https://example.org/c')
            download_chapter(client, chapter, root, False, write_manifest=True)
            output = next(root.glob('*.pdf'))
            output.write_bytes(b'old pdf')
            download_chapter(client, chapter, root, False, rebuild=True)
            self.assertEqual(len(PdfReader(output).pages), 1)
            self.assertEqual(client.get.call_count, 1)
            self.assertFalse(list(root.glob('*.pages.sha256')))

    def test_manifest_tracks_quality(self):
        client = MagicMock(spec=['chapter_html', 'get'])
        client.chapter_html.return_value = '<section id="chapter-images"><img src="/image"></section>'
        client.get.return_value = image_bytes('PNG')
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            chapter = Chapter('Test Chapter 1', 'https://example.org/c')
            download_chapter(client, chapter, root, False, write_manifest=True)
            manifest = next(root.glob('*.pages.sha256'))
            before = manifest.read_text()
            download_chapter(client, chapter, root, False, write_manifest=True, jpeg_quality=80)
            self.assertNotEqual(before, manifest.read_text())
            self.assertEqual(client.get.call_count, 1)

    def test_invalid_cli_parameters(self):
        for option, value in (('--jpeg-quality', '0'), ('--jpeg-quality', '96'),
                              ('--pdf-tasks-per-child', '0'), ('--timeout', 'nan'), ('--delay', 'inf')):
            with self.subTest(option=option, value=value), patch('sys.stderr', io.StringIO()):
                with self.assertRaises(SystemExit) as exc:
                    main(['https://example.org/rss', option, value])
                self.assertEqual(exc.exception.code, 2)


if __name__ == '__main__':
    unittest.main()
