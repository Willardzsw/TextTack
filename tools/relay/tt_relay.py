#!/usr/bin/env python3
"""TextTack 翻译转发服务（tt-relay）。

朋友在 TextTack 里选“云翻译”，填自己的 TT码（也可以用“OpenAI 兼容服务”填这台服务器的地址）。本程序核对 TT码，
把请求换成服务器上的 OpenAI 或 DeepSeek Key 转发出去，按用量折算金额并按天限额。

- 只用 Python 标准库（3.10+），不需要安装任何依赖。
- TT码只保存 SHA-256 摘要；Key 放在单独的文件里，本程序从不打印。
- 不保存、不记录翻译内容，只按朋友记每天花了多少钱。

用法（在服务器上以 root 运行管理命令）：
  python3 tt_relay.py serve
  python3 tt_relay.py add-friend 小王 [--daily 1.0]
  python3 tt_relay.py new-code 小王
  python3 tt_relay.py set-daily 小王 2.0
  python3 tt_relay.py remove-friend 小王
  python3 tt_relay.py list
  python3 tt_relay.py usage [--days 7]
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import secrets
import sys
import tempfile
import threading
import time
import http.client
import ssl
from collections import deque
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlsplit

DEFAULT_CONFIG = "/etc/tt-relay/config.json"
DEFAULT_KEYS = "/etc/tt-relay/keys.json"
DEFAULT_USAGE = "/var/lib/tt-relay/usage.json"

# 曼谷时间（UTC+7）零点换日。
LOCAL_TZ = timezone(timedelta(hours=7))

# 只转发这些字段，其他一律丢掉（例如 tools、web search、n、stream），免得产生额外费用。
ALLOWED_FIELDS = ("model", "messages", "temperature", "top_p", "seed",
                  "max_completion_tokens", "max_tokens", "stop")

MAX_BODY_BYTES = 1_000_000
KEEP_USAGE_DAYS = 90


def code_hash(code: str) -> str:
    return hashlib.sha256(code.strip().encode("utf-8")).hexdigest()


def new_code() -> str:
    return "tt-" + secrets.token_urlsafe(18)


def today() -> str:
    return datetime.now(LOCAL_TZ).date().isoformat()


def read_json(path: str, default):
    try:
        with open(path, "r", encoding="utf-8") as f:
            return json.load(f)
    except FileNotFoundError:
        return default


def write_json_atomic(path: str, data) -> None:
    """Write beside the old file and swap it in, keeping the old owner and mode."""
    folder = os.path.dirname(os.path.abspath(path))
    try:
        old = os.stat(path)
    except FileNotFoundError:
        old = None
    fd, tmp = tempfile.mkstemp(dir=folder, prefix=".tmp-", suffix=".json")
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=2)
            f.write("\n")
        if old is not None:
            os.chmod(tmp, old.st_mode & 0o777)
            if hasattr(os, "chown") and os.name == "posix" and os.geteuid() == 0:
                os.chown(tmp, old.st_uid, old.st_gid)
        os.replace(tmp, path)
    except BaseException:
        try:
            os.unlink(tmp)
        except FileNotFoundError:
            pass
        raise


# Statuses the TextTack 云翻译 service reads: 401 bad code, 403 code turned off, 402 today's
# allowance used up, 429 too many requests a minute, 502 a problem with the server's own account,
# 503 that model has no key, 504 the upstream did not answer.
class RelayError(Exception):
    def __init__(self, status: int, code: str, message: str):
        super().__init__(message)
        self.status = status
        self.code = code
        self.message = message


class Relay:
    """Everything the server knows: the config (re-read when it changes), the keys, the usage."""

    def __init__(self, config_path: str, keys_path: str, usage_path: str):
        self.config_path = config_path
        self.keys_path = keys_path
        self.usage_path = usage_path
        self._lock = threading.Lock()
        self._config: dict = {}
        self._config_mtime = None
        self._keys: dict = {}
        self._keys_mtime = None
        self._usage = read_json(usage_path, {})
        self._recent: dict[str, deque] = {}
        self.reload()

    # ---- configuration -------------------------------------------------

    def reload(self) -> None:
        """Re-read the config and keys if either file changed since the last look."""
        with self._lock:
            mtime = os.stat(self.config_path).st_mtime_ns
            if mtime != self._config_mtime:
                config = read_json(self.config_path, None)
                if config is None:
                    raise SystemExit(f"找不到配置文件：{self.config_path}")
                validate_config(config)
                self._config, self._config_mtime = config, mtime
            try:
                kmtime = os.stat(self.keys_path).st_mtime_ns
            except FileNotFoundError:
                kmtime = None
            if kmtime != self._keys_mtime:
                self._keys = read_json(self.keys_path, {}) if kmtime is not None else {}
                self._keys_mtime = kmtime

    @property
    def config(self) -> dict:
        return self._config

    # ---- one request ---------------------------------------------------

    def friend_for(self, authorization: str | None) -> str:
        if not authorization or not authorization.lower().startswith("bearer "):
            raise RelayError(401, "invalid_code", "请填写 TT码。")
        digest = code_hash(authorization[7:])
        for name, friend in self._config.get("friends", {}).items():
            if secrets.compare_digest(friend.get("code_sha256", ""), digest):
                if friend.get("disabled"):
                    raise RelayError(403, "disabled", "这个 TT码已停用。")
                return name
        raise RelayError(401, "invalid_code", "TT码不对，请检查有没有多复制空格。")

    def quota(self, friend: str) -> dict:
        """What one friend has used today, for the app's 今日额度 button."""
        cfg = self._config
        cap = float(cfg["friends"][friend].get("daily_cny", cfg.get("default_daily_cny", 1.0)))
        with self._lock:
            used = self._usage.get(today(), {}).get(friend, {})
        spent = round(float(used.get("cny", 0.0)), 4)
        return {
            "date": today(),
            "used_cny": spent,
            "daily_cny": cap,
            "remaining_cny": round(max(0.0, cap - spent), 4),
            "requests": int(used.get("requests", 0)),
            "models": [m for m, spec in cfg["models"].items()
                       if str(self._keys.get(spec["upstream"], "")).strip()],
        }

    def check_limits(self, friend: str) -> None:
        cfg = self._config
        per_minute = int(cfg.get("requests_per_minute", 30))
        now = time.monotonic()
        with self._lock:
            recent = self._recent.setdefault(friend, deque())
            while recent and now - recent[0] > 60:
                recent.popleft()
            if len(recent) >= per_minute:
                raise RelayError(429, "rate_limited", "请求太频繁，请稍等一分钟再试。")
            recent.append(now)

            day = self._usage.get(today(), {})
            cap = float(cfg["friends"][friend].get("daily_cny", cfg.get("default_daily_cny", 1.0)))
            if day.get(friend, {}).get("cny", 0.0) >= cap:
                raise RelayError(402, "daily_quota", "今天的额度已经用完，曼谷时间零点后恢复。")
            total_cap = cfg.get("total_daily_cny")
            if total_cap is not None and sum(v.get("cny", 0.0) for v in day.values()) >= float(total_cap):
                raise RelayError(402, "total_quota", "今天所有人的总额度已经用完，曼谷时间零点后恢复。")

    def prepare(self, body: dict) -> tuple[str, dict, str, str]:
        """The model to use, the payload to send, the upstream URL and its key."""
        cfg = self._config
        models = cfg["models"]
        model = str(body.get("model") or "").strip()
        if model not in models:
            model = cfg["default_model"]
        spec = models[model]
        upstream = cfg["upstreams"][spec["upstream"]]
        key = str(self._keys.get(spec["upstream"], "")).strip()
        if not key:
            raise RelayError(503, "model_unavailable", f"{model} 暂时不可用，请换另一个模型。")

        if not isinstance(body.get("messages"), list) or not body["messages"]:
            raise RelayError(400, "bad_request", "请求里没有要翻译的内容。")

        payload = {k: body[k] for k in ALLOWED_FIELDS if k in body}
        payload["model"] = spec.get("upstream_model", model)
        for field in upstream.get("drop_fields", []):
            payload.pop(field, None)
        payload["stream"] = False
        if spec.get("reasoning_effort"):
            payload["reasoning_effort"] = spec["reasoning_effort"]

        cap = int(cfg.get("max_output_tokens", 8192))
        has_limit = False
        for field in ("max_completion_tokens", "max_tokens"):
            if field in payload:
                has_limit = True
                try:
                    payload[field] = max(1, min(int(payload[field]), cap))
                except (TypeError, ValueError):
                    payload[field] = cap
        if not has_limit:
            payload["max_completion_tokens"] = cap
        return model, payload, upstream["url"], key

    def charge(self, friend: str, model: str, usage: dict | None) -> float:
        spec = self._config["models"][model]
        prompt = int((usage or {}).get("prompt_tokens") or 0)
        completion = int((usage or {}).get("completion_tokens") or 0)
        cost = (prompt * float(spec["input_cny_per_m"]) + completion * float(spec["output_cny_per_m"])) / 1_000_000
        with self._lock:
            day_key = today()
            day = self._usage.setdefault(day_key, {})
            entry = day.setdefault(friend, {"cny": 0.0, "requests": 0, "prompt_tokens": 0, "completion_tokens": 0})
            entry["cny"] = round(entry["cny"] + cost, 6)
            entry["requests"] += 1
            entry["prompt_tokens"] += prompt
            entry["completion_tokens"] += completion
            cutoff = (datetime.now(LOCAL_TZ).date() - timedelta(days=KEEP_USAGE_DAYS)).isoformat()
            for old in [d for d in self._usage if d < cutoff]:
                del self._usage[old]
            write_json_atomic(self.usage_path, self._usage)
        return cost


def validate_config(config: dict) -> None:
    for field in ("models", "default_model", "upstreams", "friends"):
        if field not in config:
            raise SystemExit(f"配置文件缺少 {field}")
    if config["default_model"] not in config["models"]:
        raise SystemExit("default_model 不在 models 里")
    for name, spec in config["models"].items():
        if spec.get("upstream") not in config["upstreams"]:
            raise SystemExit(f"模型 {name} 的 upstream 不在 upstreams 里")
        for price in ("input_cny_per_m", "output_cny_per_m"):
            if not isinstance(spec.get(price), (int, float)) or spec[price] <= 0:
                raise SystemExit(f"模型 {name} 缺少单价 {price}")


def error_body(code: str, message: str) -> bytes:
    return json.dumps({"error": {"message": message, "type": "tt_relay", "code": code}},
                      ensure_ascii=False).encode("utf-8")


class UpstreamPool:
    """Keeps connections to OpenAI and DeepSeek open between requests.

    A new HTTPS connection costs a TCP and a TLS handshake before the request can even be sent;
    to a server far from Singapore that is a noticeable part of a short translation. Idle
    connections are kept for a while and handed to the next request for the same host.
    """

    IDLE_SECONDS = 50       # servers close idle connections after a minute or so
    MAX_IDLE_PER_HOST = 8

    def __init__(self, timeout: float):
        self.timeout = timeout
        self._idle: dict[tuple, list] = {}
        self._lock = threading.Lock()
        self._tls = ssl.create_default_context()

    def _new(self, key: tuple):
        scheme, host, port = key
        if scheme == "https":
            return http.client.HTTPSConnection(host, port, timeout=self.timeout, context=self._tls)
        return http.client.HTTPConnection(host, port, timeout=self.timeout)

    def _take(self, key: tuple):
        now = time.monotonic()
        with self._lock:
            stack = self._idle.get(key, [])
            while stack:
                conn, idle_since = stack.pop()
                if now - idle_since < self.IDLE_SECONDS:
                    return conn, True
                conn.close()
        return self._new(key), False

    def _give(self, key: tuple, conn) -> None:
        with self._lock:
            stack = self._idle.setdefault(key, [])
            if len(stack) < self.MAX_IDLE_PER_HOST:
                stack.append((conn, time.monotonic()))
                return
        conn.close()

    def post(self, url: str, body: bytes, headers: dict) -> tuple[int, bytes, bool]:
        """Status, body, and whether an already-open connection carried it."""
        parts = urlsplit(url)
        key = (parts.scheme, parts.hostname, parts.port)
        path = parts.path + (f"?{parts.query}" if parts.query else "")
        for attempt in (0, 1):
            conn, reused = self._take(key)
            try:
                conn.request("POST", path, body=body, headers=headers)
                response = conn.getresponse()
                data = response.read()
            except (ConnectionError, http.client.BadStatusLine, http.client.CannotSendRequest):
                conn.close()
                # The server closed a connection that sat idle: nothing was answered, so the
                # request is sent once more on a fresh one.
                if reused and attempt == 0:
                    continue
                raise
            except BaseException:
                conn.close()
                raise
            if response.will_close:
                conn.close()
            else:
                self._give(key, conn)
            return response.status, data, reused
        raise AssertionError("unreachable")


def make_handler(relay: Relay, timeout: float):
    pool = UpstreamPool(timeout)

    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"
        server_version = "tt-relay"
        sys_version = ""

        def log_message(self, fmt, *args):  # 不记录请求行，只在下面写自己的一行。
            pass

        def send_json(self, status: int, body: bytes) -> None:
            self.send_response(status)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self):
            path = self.path.split("?")[0].rstrip("/")
            if path == "/healthz":
                self.send_json(200, b'{"ok":true}')
            elif path == "/v1/quota":
                try:
                    relay.reload()
                    friend = relay.friend_for(self.headers.get("Authorization"))
                    self.send_json(200, json.dumps(relay.quota(friend), ensure_ascii=False).encode("utf-8"))
                except RelayError as e:
                    self.send_json(e.status, error_body(e.code, e.message))
            else:
                self.send_json(404, error_body("not_found", "这里只提供翻译接口。"))

        def do_POST(self):
            path = self.path.split("?")[0].rstrip("/")
            if path not in ("/v1/chat/completions", "/chat/completions"):
                self.send_json(404, error_body("not_found", "这里只提供翻译接口。"))
                return
            started = time.monotonic()
            friend, model, status = "-", "-", 0
            try:
                length = int(self.headers.get("Content-Length") or 0)
                if length <= 0 or length > MAX_BODY_BYTES:
                    raise RelayError(413 if length > 0 else 400, "bad_request", "请求内容为空或太大。")
                raw = self.rfile.read(length)
                relay.reload()
                friend = relay.friend_for(self.headers.get("Authorization"))
                try:
                    body = json.loads(raw)
                except ValueError:
                    raise RelayError(400, "bad_request", "请求不是有效的 JSON。")
                if not isinstance(body, dict):
                    raise RelayError(400, "bad_request", "请求不是有效的 JSON。")
                relay.check_limits(friend)
                model, payload, url, key = relay.prepare(body)

                status, answer, reused = pool.post(
                    url, json.dumps(payload).encode("utf-8"),
                    {"Content-Type": "application/json", "Authorization": f"Bearer {key}"})
                cost = 0.0
                if status == 200:
                    try:
                        cost = relay.charge(friend, model, json.loads(answer).get("usage"))
                    except ValueError:
                        pass
                    self.send_json(200, answer)
                else:
                    # 上游的错误原样转给朋友，只换掉 401/403，免得以为是自己的码不对。
                    if status in (401, 403):
                        status, answer = 502, error_body("upstream_auth", "服务器上的 Key 出了问题，请联系提供者。")
                    elif status == 402:
                        status, answer = 502, error_body("upstream_balance", "服务器上的账户余额不足，请联系提供者。")
                    self.send_json(status, answer)
                log(f"{friend} {model} {status} ¥{cost:.5f} {time.monotonic() - started:.2f}s"
                    f" {'reused' if reused else 'new'}")
            except RelayError as e:
                status = e.status
                self.send_json(e.status, error_body(e.code, e.message))
                log(f"{friend} {model} {status} {e.code} {time.monotonic() - started:.2f}s")
            except (TimeoutError, OSError, http.client.HTTPException) as e:
                status = 504 if isinstance(e, TimeoutError) or "timed out" in str(e) else 502
                self.send_json(status, error_body("upstream_unreachable", "连不上翻译服务，请稍后再试。"))
                log(f"{friend} {model} {status} {type(e).__name__} {time.monotonic() - started:.2f}s")

    return Handler


def log(line: str) -> None:
    print(line, flush=True)


# ---- command line ------------------------------------------------------

def cmd_serve(args) -> None:
    relay = Relay(args.config, args.keys, args.usage)
    server = ThreadingHTTPServer((args.host, args.port), make_handler(relay, args.timeout))
    server.daemon_threads = True
    missing = [u for u in relay.config["upstreams"] if not str(relay._keys.get(u, "")).strip()]
    log(f"tt-relay 在 {args.host}:{args.port} 监听；未填 Key 的服务：{', '.join(missing) or '无'}")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass


def load_config_for_edit(path: str) -> dict:
    config = read_json(path, None)
    if config is None:
        raise SystemExit(f"找不到配置文件：{path}")
    return config


def cmd_add_friend(args) -> None:
    config = load_config_for_edit(args.config)
    friends = config.setdefault("friends", {})
    if args.name in friends:
        raise SystemExit(f"{args.name} 已经存在；要换码请用 new-code。")
    code = new_code()
    friends[args.name] = {"code_sha256": code_hash(code)}
    if args.daily is not None:
        friends[args.name]["daily_cny"] = args.daily
    write_json_atomic(args.config, config)
    print(f"已添加 {args.name}。TT码（只显示这一次，请直接发给对方）：\n{code}")


def cmd_new_code(args) -> None:
    config = load_config_for_edit(args.config)
    friend = config.get("friends", {}).get(args.name)
    if friend is None:
        raise SystemExit(f"没有 {args.name}。")
    code = new_code()
    friend["code_sha256"] = code_hash(code)
    friend.pop("disabled", None)
    write_json_atomic(args.config, config)
    print(f"{args.name} 的旧码已作废。新 TT码（只显示这一次）：\n{code}")


def cmd_set_daily(args) -> None:
    config = load_config_for_edit(args.config)
    friend = config.get("friends", {}).get(args.name)
    if friend is None:
        raise SystemExit(f"没有 {args.name}。")
    friend["daily_cny"] = args.daily
    write_json_atomic(args.config, config)
    print(f"{args.name} 每天上限改为 ¥{args.daily}。")


def cmd_remove_friend(args) -> None:
    config = load_config_for_edit(args.config)
    if config.get("friends", {}).pop(args.name, None) is None:
        raise SystemExit(f"没有 {args.name}。")
    write_json_atomic(args.config, config)
    print(f"已删除 {args.name}，他的 TT码立即失效。")


def cmd_list(args) -> None:
    config = load_config_for_edit(args.config)
    day = read_json(args.usage, {}).get(today(), {})
    default = config.get("default_daily_cny", 1.0)
    print(f"今天（{today()}，曼谷时间）")
    for name, friend in config.get("friends", {}).items():
        used = day.get(name, {})
        cap = friend.get("daily_cny", default)
        state = "（已停用）" if friend.get("disabled") else ""
        print(f"  {name}{state}：¥{used.get('cny', 0):.4f} / ¥{cap}，{used.get('requests', 0)} 次")
    if not config.get("friends"):
        print("  还没有朋友，用 add-friend 添加。")


def cmd_usage(args) -> None:
    usage = read_json(args.usage, {})
    for d in sorted(usage)[-args.days:]:
        total = sum(v.get("cny", 0) for v in usage[d].values())
        print(f"{d}  合计 ¥{total:.4f}")
        for name, v in sorted(usage[d].items()):
            print(f"    {name}：¥{v.get('cny', 0):.4f}，{v.get('requests', 0)} 次，"
                  f"输入 {v.get('prompt_tokens', 0)} / 输出 {v.get('completion_tokens', 0)} token")
    if not usage:
        print("还没有用量记录。")


def main(argv=None) -> None:
    parser = argparse.ArgumentParser(description="TextTack 翻译转发服务")
    parser.add_argument("--config", default=os.environ.get("TT_RELAY_CONFIG", DEFAULT_CONFIG))
    parser.add_argument("--keys", default=os.environ.get("TT_RELAY_KEYS", DEFAULT_KEYS))
    parser.add_argument("--usage", default=os.environ.get("TT_RELAY_USAGE", DEFAULT_USAGE))
    sub = parser.add_subparsers(dest="command", required=True)

    serve = sub.add_parser("serve", help="运行转发服务")
    serve.add_argument("--host", default="127.0.0.1")
    serve.add_argument("--port", type=int, default=8230)
    serve.add_argument("--timeout", type=float, default=120.0)
    serve.set_defaults(func=cmd_serve)

    p = sub.add_parser("add-friend", help="添加朋友并生成 TT码")
    p.add_argument("name")
    p.add_argument("--daily", type=float, help="每天上限（人民币），不填用默认值")
    p.set_defaults(func=cmd_add_friend)

    p = sub.add_parser("new-code", help="给朋友换一个新码，旧码作废")
    p.add_argument("name")
    p.set_defaults(func=cmd_new_code)

    p = sub.add_parser("set-daily", help="修改朋友每天上限（人民币）")
    p.add_argument("name")
    p.add_argument("daily", type=float)
    p.set_defaults(func=cmd_set_daily)

    p = sub.add_parser("remove-friend", help="删除朋友，他的码立即失效")
    p.add_argument("name")
    p.set_defaults(func=cmd_remove_friend)

    sub.add_parser("list", help="看每个朋友今天用了多少").set_defaults(func=cmd_list)

    p = sub.add_parser("usage", help="看最近几天的用量")
    p.add_argument("--days", type=int, default=7)
    p.set_defaults(func=cmd_usage)

    args = parser.parse_args(argv)
    args.func(args)


if __name__ == "__main__":
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    main()
