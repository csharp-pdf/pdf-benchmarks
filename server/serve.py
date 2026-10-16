"""Serves fixtures/ on http://127.0.0.1:8777 for the duration of a run.

    python server/serve.py [port]

A plain static server: no compression, no artificial latency. Every library
fetches the same files the same way, so network variance is out of the results.

Two endpoints exist for the timeout and error tests (METHODOLOGY.md, 4.12):
  /_delay/<ms>/<path>   serves fixtures/<path> after waiting <ms> milliseconds
  /_hang                accepts the request and never answers (until the client gives up)
"""
import functools
import http.server
import pathlib
import sys
import time

ROOT = pathlib.Path(__file__).resolve().parents[1] / "fixtures"


class Handler(http.server.SimpleHTTPRequestHandler):
    extensions_map = {**http.server.SimpleHTTPRequestHandler.extensions_map,
                      ".woff2": "font/woff2", ".js": "text/javascript", ".css": "text/css"}

    def do_GET(self):
        if self.path.startswith("/_hang"):
            time.sleep(3600)   # never answers within any library's timeout
            return
        if self.path.startswith("/_delay/"):
            _, _, ms, rest = self.path.split("/", 3)
            time.sleep(int(ms) / 1000)
            self.path = "/" + rest
        super().do_GET()

    def log_message(self, *args):   # quiet: the runner's output is the record
        pass


if __name__ == "__main__":
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 8777
    server = http.server.ThreadingHTTPServer(("127.0.0.1", port), functools.partial(Handler, directory=str(ROOT)))
    server.daemon_threads = True
    print(f"serving {ROOT} on http://127.0.0.1:{port}", flush=True)
    server.serve_forever()
