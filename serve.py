#!/usr/bin/env python3
"""Static server for the Unity WebGL build in Build/tiger.

The build ships Brotli-compressed .br files with no JS decompression fallback,
so the browser only accepts them when the response carries Content-Encoding: br
plus the *uncompressed* file's Content-Type. python3 -m http.server does neither.

Usage: python3 serve.py [port]   (default 8080)
"""

import functools
import http.server
import os
import sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "Build", "tiger")

TYPES = {
    ".js": "application/javascript",
    ".wasm": "application/wasm",
    ".data": "application/octet-stream",
    ".symbols.json": "application/octet-stream",
}

ENCODINGS = {".br": "br", ".gz": "gzip"}


class UnityHandler(http.server.SimpleHTTPRequestHandler):
    def end_headers(self):
        path = self.translate_path(self.path)
        _, ext = os.path.splitext(path)
        encoding = ENCODINGS.get(ext)
        if encoding:
            self.send_header("Content-Encoding", encoding)
        # No caching, so a rebuild shows up on a plain reload.
        self.send_header("Cache-Control", "no-store")
        super().end_headers()

    def guess_type(self, path):
        # Strip .br/.gz first: the type describes the decompressed payload.
        base, ext = os.path.splitext(path)
        if ext in ENCODINGS:
            path, ext = base, os.path.splitext(base)[1]
        return TYPES.get(ext) or super().guess_type(path)

    def log_message(self, fmt, *args):
        sys.stderr.write("%s %s\n" % (self.address_string(), fmt % args))


def main():
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 8080
    if not os.path.isfile(os.path.join(ROOT, "index.html")):
        sys.exit("No index.html in %s — build the WebGL player first." % ROOT)

    handler = functools.partial(UnityHandler, directory=ROOT)
    server = http.server.ThreadingHTTPServer(("0.0.0.0", port), handler)
    print("Serving %s at http://localhost:%d/  (Ctrl+C to stop)" % (ROOT, port))
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\nStopped.")


if __name__ == "__main__":
    main()
