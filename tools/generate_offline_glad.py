"""Use the fork's pinned Khronos XML with GLAD's offline, reproducible generator.

GLAD's stock --reproducible opener replaces even file: URLs with its older bundled
XML. Retain the fork's VK_KHR_maintenance9 specification while keeping network IO off.
The GLAD submodule itself is left unchanged.
"""
import sys
from pathlib import Path
from urllib.parse import urlparse

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'deps/glad'))


def main():
    import glad.files
    import glad.__main__

    class PinnedOpener(glad.files.StaticFileOpener):
        def urlopen(self, url, data=None, *args, **kwargs):
            if data is not None:
                raise ValueError('GLAD generation cannot send a request body')
            name = urlparse(url).path.rsplit('/', 1)[-1]
            if name in {'gl.xml', 'wgl.xml', 'vk.xml'}:
                return (ROOT / 'deps/khronos' / name).open('rb')
            return super().urlopen(url, data, *args, **kwargs)

    glad.files.StaticFileOpener = PinnedOpener
    return glad.__main__.main(['--reproducible', *sys.argv[1:]])


if __name__ == '__main__':
    sys.exit(main())
