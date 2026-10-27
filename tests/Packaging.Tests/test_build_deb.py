"""Offline checks for the cross-platform Debian package writer."""
import importlib.util
import io
from pathlib import Path
import struct
import tarfile
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location(
    "build_deb", Path(__file__).resolve().parents[2] / "scripts/build-deb.py"
)
deb = importlib.util.module_from_spec(spec)
spec.loader.exec_module(deb)


def read_ar(path):
    members = {}
    with path.open("rb") as source:
        assert source.read(8) == b"!<arch>\n"
        while header := source.read(60):
            assert len(header) == 60 and header[58:] == b"`\n"
            size = int(header[48:58])
            members[header[:16].decode().strip().rstrip("/")] = source.read(size)
            if size % 2:
                assert source.read(1) == b"\n"
    return members


class PackageTests(unittest.TestCase):
    def test_safe_paths(self):
        for name in ("/etc/passwd", "../escape", "chrome/../../escape", "C:/escape", "a\\b"):
            with self.subTest(name=name), self.assertRaises(ValueError):
                deb.safe_path(name)
        self.assertEqual(deb.safe_path("chrome-linux/chrome"), "chrome-linux/chrome")

    def test_executable_modes(self):
        self.assertEqual(deb.executable_mode(b"\x7fELF"), 0o755)
        self.assertEqual(deb.executable_mode(b"#!/b"), 0o755)
        self.assertEqual(deb.executable_mode(b"data"), 0o644)
        self.assertEqual(deb.executable_mode(b"data", 0o755), 0o755)

    def test_package_payload_and_control(self):
        temporary_root = deb.ROOT / "tmp/packaging-tests"
        temporary_root.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=temporary_root) as directory:
            root = Path(directory)
            publish = root / "publish"
            driver = publish / ".playwright/package"
            driver.mkdir(parents=True)
            (driver / "cli.js").write_text("// fixture", encoding="utf-8")
            (driver / "browsers.json").write_text("{}", encoding="utf-8")
            stale_browser = publish / "browsers/chromium-123/chrome"
            stale_browser.parent.mkdir(parents=True)
            stale_browser.write_bytes(b"old browser")
            (publish / "Microsoft.Playwright.dll").write_bytes(b"MZ fixture")
            node = publish / ".playwright/node/linux-x64/node"
            node.parent.mkdir(parents=True)
            header = b"\x7fELF\x02\x01" + bytes(12) + struct.pack("<H", 62)
            node.write_bytes(header)
            (publish / "manga_leech").write_bytes(header)
            args = SimpleNamespace(runtime="linux-x64", version="1.2.3-1~deb13", no_build=True,
                                   publish_dir=publish, output_dir=root / "output",
                                   maintainer="Test Maintainer")
            with patch.object(deb, "run") as run:
                target = deb.build(args)
                run.assert_not_called()  # No Node.js, browser downloads or install hooks.
            members = read_ar(target)
            self.assertEqual(list(members), ["debian-binary", "control.tar.gz", "data.tar.gz"])
            self.assertEqual(members["debian-binary"], b"2.0\n")
            with tarfile.open(fileobj=io.BytesIO(members["control.tar.gz"]), mode="r:gz") as control:
                text = control.extractfile("control").read().decode()
                self.assertIn("Architecture: amd64\n", text)
                self.assertIn("libicu76", text)
                self.assertIn("libssl3t64", text)
                self.assertNotIn("chromium", text)
                self.assertNotIn("nodejs (", text)
                digests = control.extractfile("md5sums").read().decode()
                self.assertIn("usr/bin/manga_leech", digests)
            with tarfile.open(fileobj=io.BytesIO(members["data.tar.gz"]), mode="r:gz") as data:
                self.assertFalse(any(name == f"{deb.APP}/browsers" or name.startswith(f"{deb.APP}/browsers/") or ".local-browsers" in name for name in data.getnames()))
                self.assertFalse(any("/.playwright" in name for name in data.getnames()))
                for name in ("usr/bin/manga_leech", f"{deb.APP}/manga_leech"):
                    self.assertEqual(data.getmember(name).mode, 0o755)
                    self.assertEqual(data.getmember(name).uid, 0)
                launcher = data.extractfile("usr/bin/manga_leech").read()
                self.assertNotIn(b"PLAYWRIGHT_", launcher)
                self.assertNotIn(b"MANGA_LEECH_BROWSER_", launcher)
                self.assertIn(b'"$@"', launcher)
                self.assertNotIn(f"{deb.APP}/Microsoft.Playwright.dll", data.getnames())
                self.assertNotIn(f"{deb.APP}/.playwright/package/browsers.json", data.getnames())
            (publish / "manga_leech").write_bytes(b"MZ" + bytes(30))
            with self.assertRaises(ValueError):
                deb.validate_publish(publish, "linux-x64")


if __name__ == "__main__":
    unittest.main()
