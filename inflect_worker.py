#!/usr/bin/env python3
"""Warm Inflect-Micro-v2 ONNX worker for ReadAloud.

Loads the model once, then answers one-shot JSON requests over a Unix socket.

Request line (JSON):
  {"text":"...", "output":"/tmp/out.wav", "speed":1.0, "variation":0.667, "seed":7}

Response line:
  ok
  err <message>

Env:
  READALOUD_INFLECT_MODEL  model dir (contains onnx/inference_onnx.py)
  READALOUD_INFLECT_SOCK   socket path
"""
from __future__ import annotations

import json
import os
import socket
import sys
import traceback
from pathlib import Path


def main() -> int:
    model = Path(os.environ["READALOUD_INFLECT_MODEL"]).resolve()
    sock_path = Path(os.environ["READALOUD_INFLECT_SOCK"])
    sys.path.insert(0, str(model / "onnx"))
    from inference_onnx import InflectONNX  # noqa: E402

    tts = InflectONNX(str(model), provider="cpu")

    if sock_path.exists():
        sock_path.unlink()
    sock_path.parent.mkdir(parents=True, exist_ok=True)

    server = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    server.bind(str(sock_path))
    server.listen(8)
    os.chmod(sock_path, 0o600)

    # file-based ready signal so the C# parent never has to keep stdout open
    ready_path = Path(os.environ.get("READALOUD_INFLECT_READY", str(sock_path) + ".ready"))
    ready_path.write_text("ok\n", encoding="utf-8")

    while True:
        conn, _ = server.accept()
        with conn:
            try:
                data = b""
                while not data.endswith(b"\n"):
                    chunk = conn.recv(65536)
                    if not chunk:
                        break
                    data += chunk
                if not data:
                    continue
                req = json.loads(data.decode("utf-8"))
                cmd = req.get("cmd")
                if cmd == "quit":
                    conn.sendall(b"ok\n")
                    break
                if cmd == "ping":
                    # health check: model is loaded, socket is live — no synth
                    conn.sendall(b"ok\n")
                    continue
                text = (req.get("text") or "").strip()
                output = req.get("output") or ""
                if not text or not output:
                    conn.sendall(b"err empty text or output\n")
                    continue
                # progress to stderr so a tail of the log shows what the long silence is
                sys.stderr.write(f"synth {len(text)} chars → {output}\n")
                sys.stderr.flush()
                tts.save(
                    text,
                    output,
                    speed=float(req.get("speed", 1.0)),
                    variation=float(req.get("variation", 0.667)),
                    seed=int(req.get("seed", 0)),
                )
                conn.sendall(b"ok\n")
            except Exception as exc:  # noqa: BLE001 — surface any synth failure to C#
                msg = f"err {exc}\n".encode("utf-8", errors="replace")
                try:
                    conn.sendall(msg)
                except OSError:
                    pass
                traceback.print_exc()

    server.close()
    try:
        sock_path.unlink()
    except OSError:
        pass
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
