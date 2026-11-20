# -*- coding: utf-8 -*-
"""A tiny MCP client over streamable HTTP (standard library only), for MCPCon.
Copied from the lab's sf6/pilot.py by tools/export_public.py."""
import json
import threading
import urllib.request


class Mcp(object):
    """A tiny MCP client over streamable HTTP (standard library only)."""

    def __init__(self, url):
        self.url = url.rstrip("/") + "/"
        self.sid = None
        self.lock = threading.Lock()
        self.n = 0

    def _rpc(self, msg, timeout):
        h = {"Content-Type": "application/json", "Accept": "application/json, text/event-stream"}
        if self.sid:
            h["Mcp-Session-Id"] = self.sid
        req = urllib.request.Request(self.url, data=json.dumps(msg).encode("utf-8"), headers=h)
        with urllib.request.urlopen(req, timeout=timeout) as r:
            self.sid = r.headers.get("Mcp-Session-Id") or self.sid
            text = r.read().decode("utf-8")
        data = None
        for line in text.splitlines():
            if line.startswith("data:"):
                data = json.loads(line[5:])
        if data is None and text.strip():
            data = json.loads(text)
        return data

    def _ensure(self):
        if self.sid:
            return
        self._rpc({"jsonrpc": "2.0", "id": 0, "method": "initialize", "params": {
            "protocolVersion": "2025-03-26", "capabilities": {},
            "clientInfo": {"name": "voice-pilot", "version": "1"}}}, 10)
        self._rpc({"jsonrpc": "2.0", "method": "notifications/initialized"}, 10)

    def call(self, tool, arguments, timeout=10):
        with self.lock:
            self._ensure()
            self.n += 1
            n = self.n
        res = self._rpc({"jsonrpc": "2.0", "id": n, "method": "tools/call",
                         "params": {"name": tool, "arguments": arguments}}, timeout)
        if res and "error" in res:
            raise RuntimeError(res["error"].get("message", str(res["error"])))
        out = (res or {}).get("result", {})
        text = " ".join(c.get("text", "") for c in out.get("content", []))
        if out.get("isError"):
            raise RuntimeError(text)
        return text
