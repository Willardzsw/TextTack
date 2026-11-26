"""tt-relay 的本地测试：上游是本机上的假服务器，Key 和 TT码都是测试用的假值。

运行：python -m unittest tools/relay/test_tt_relay.py -v
"""

from __future__ import annotations

import json
import os
import shutil
import sys
import tempfile
import threading
import time
import unittest
import urllib.error
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import tt_relay  # noqa: E402

FAKE_OPENAI_KEY = "fake-openai-key"
FAKE_DEEPSEEK_KEY = "fake-deepseek-key"


class FakeUpstream:
    """Records what it is sent and answers like a chat-completions server."""

    def __init__(self):
        self.requests = []
        self.status = 200
        self.close_silently = False  # drop the connection after answering, without saying so
        outer = self

        class Handler(BaseHTTPRequestHandler):
            protocol_version = "HTTP/1.1"

            def log_message(self, *a):
                pass

            def do_POST(self):
                body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                outer.requests.append({"path": self.path, "auth": self.headers.get("Authorization"), "body": body,
                                       "port": self.client_address[1]})
                if outer.status == 200:
                    answer = {"choices": [{"message": {"role": "assistant", "content": "สวัสดี"}}],
                              "usage": {"prompt_tokens": 1000, "completion_tokens": 500}}
                else:
                    answer = {"error": {"message": "upstream says no"}}
                data = json.dumps(answer, ensure_ascii=False).encode()
                self.send_response(outer.status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)
                if outer.close_silently:
                    self.close_connection = True

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        threading.Thread(target=self.server.serve_forever, daemon=True).start()
        self.base = f"http://127.0.0.1:{self.server.server_address[1]}"

    def close(self):
        self.server.shutdown()
        self.server.server_close()


class RelayTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp(prefix="tt-relay-test-")
        self.upstream = FakeUpstream()
        with open(os.path.join(os.path.dirname(__file__), "config.example.json"), encoding="utf-8") as f:
            config = json.load(f)
        config["upstreams"]["openai"]["url"] = self.upstream.base + "/openai/v1/chat/completions"
        config["upstreams"]["deepseek"]["url"] = self.upstream.base + "/deepseek/chat/completions"
        self.config_path = os.path.join(self.dir, "config.json")
        self.keys_path = os.path.join(self.dir, "keys.json")
        self.usage_path = os.path.join(self.dir, "usage.json")
        tt_relay.write_json_atomic(self.config_path, config)
        tt_relay.write_json_atomic(self.keys_path, {"openai": FAKE_OPENAI_KEY, "deepseek": FAKE_DEEPSEEK_KEY})
        self.code = self.add_friend("小王")
        self.start_relay()

    def tearDown(self):
        self.relay_server.shutdown()
        self.relay_server.server_close()
        self.upstream.close()
        shutil.rmtree(self.dir, ignore_errors=True)

    # ---- helpers -------------------------------------------------------

    def cli(self, *args):
        tt_relay.main(["--config", self.config_path, "--keys", self.keys_path, "--usage", self.usage_path, *args])

    def add_friend(self, name, *extra):
        from io import StringIO
        from contextlib import redirect_stdout
        out = StringIO()
        with redirect_stdout(out):
            self.cli("add-friend", name, *extra)
        return out.getvalue().strip().splitlines()[-1]

    def start_relay(self):
        self.relay = tt_relay.Relay(self.config_path, self.keys_path, self.usage_path)
        self.relay_server = ThreadingHTTPServer(("127.0.0.1", 0), tt_relay.make_handler(self.relay, 10))
        self.relay_server.daemon_threads = True
        threading.Thread(target=self.relay_server.serve_forever, daemon=True).start()
        self.url = f"http://127.0.0.1:{self.relay_server.server_address[1]}"

    def edit_config(self, change):
        with open(self.config_path, encoding="utf-8") as f:
            config = json.load(f)
        change(config)
        time.sleep(0.02)  # a different mtime
        tt_relay.write_json_atomic(self.config_path, config)

    def post(self, body, code=None, path="/v1/chat/completions"):
        code = self.code if code is None else code
        headers = {"Content-Type": "application/json"}
        if code:
            headers["Authorization"] = f"Bearer {code}"
        req = urllib.request.Request(self.url + path, data=json.dumps(body).encode(), headers=headers, method="POST")
        try:
            with urllib.request.urlopen(req, timeout=10) as r:
                return r.status, json.loads(r.read())
        except urllib.error.HTTPError as e:
            return e.code, json.loads(e.read())

    def translate(self, model="gpt-6-luna", **extra):
        return self.post({"model": model, "messages": [{"role": "user", "content": "hello"}], **extra})

    # ---- tests ---------------------------------------------------------

    def test_codes_are_stored_only_as_a_hash(self):
        with open(self.config_path, encoding="utf-8") as f:
            text = f.read()
        self.assertTrue(self.code.startswith("tt-"))
        self.assertNotIn(self.code, text)
        self.assertIn(tt_relay.code_hash(self.code), text)

    def test_wrong_or_missing_code_is_refused_and_nothing_is_sent(self):
        self.assertEqual(self.post({"messages": [{"role": "user", "content": "x"}]}, code="tt-wrong")[0], 401)
        self.assertEqual(self.post({"messages": [{"role": "user", "content": "x"}]}, code="")[0], 401)
        self.assertEqual(self.upstream.requests, [])

    def test_openai_request_gets_the_server_key_and_only_safe_fields(self):
        status, answer = self.translate(
            temperature=0.3, seed=7, max_completion_tokens=999999, service_tier="fast",
            reasoning_effort="high", tools=[{"type": "web_search"}], n=5, stream=True)
        self.assertEqual(status, 200)
        self.assertEqual(answer["choices"][0]["message"]["content"], "สวัสดี")
        sent = self.upstream.requests[-1]
        self.assertEqual(sent["path"], "/openai/v1/chat/completions")
        self.assertEqual(sent["auth"], f"Bearer {FAKE_OPENAI_KEY}")
        body = sent["body"]
        self.assertEqual(body["model"], "gpt-6-luna")
        self.assertEqual(body["reasoning_effort"], "none")
        self.assertEqual(body["max_completion_tokens"], 8192)
        self.assertEqual(body["seed"], 7)
        self.assertEqual(body["temperature"], 0.3)
        self.assertFalse(body["stream"])
        for gone in ("service_tier", "tools", "n"):
            self.assertNotIn(gone, body)

    def test_deepseek_goes_to_deepseek_without_seed(self):
        self.assertEqual(self.translate("deepseek-flash", seed=7)[0], 200)
        sent = self.upstream.requests[-1]
        self.assertEqual(sent["path"], "/deepseek/chat/completions")
        self.assertEqual(sent["auth"], f"Bearer {FAKE_DEEPSEEK_KEY}")
        self.assertEqual(sent["body"]["model"], "deepseek-flash")
        self.assertEqual(sent["body"]["reasoning_effort"], "none")
        self.assertNotIn("seed", sent["body"])

    def test_empty_or_unknown_model_uses_openai(self):
        for model in ("", "deepseek-v4-pro", "gpt-6"):
            self.assertEqual(self.translate(model)[0], 200)
            self.assertEqual(self.upstream.requests[-1]["body"]["model"], "gpt-6-luna")
            self.assertEqual(self.upstream.requests[-1]["auth"], f"Bearer {FAKE_OPENAI_KEY}")

    def test_a_limit_is_always_sent(self):
        self.translate()
        self.assertEqual(self.upstream.requests[-1]["body"]["max_completion_tokens"], 8192)
        self.translate(max_completion_tokens=300)
        self.assertEqual(self.upstream.requests[-1]["body"]["max_completion_tokens"], 300)

    def test_usage_is_charged_at_the_model_price(self):
        self.translate()                 # 1000 in, 500 out at 0.75 / 3.6 per M
        self.translate("deepseek-flash") # 1000 in, 500 out at 2 / 8 per M
        with open(self.usage_path, encoding="utf-8") as f:
            entry = json.load(f)[tt_relay.today()]["小王"]
        self.assertAlmostEqual(entry["cny"], 0.00255 + 0.006, places=6)
        self.assertEqual(entry["requests"], 2)
        self.assertEqual(entry["prompt_tokens"], 2000)

    def test_usage_file_holds_no_text(self):
        self.translate()
        with open(self.usage_path, encoding="utf-8") as f:
            text = f.read()
        self.assertNotIn("hello", text)
        self.assertNotIn("สวัสดี", text)

    def test_daily_cap_stops_a_friend_until_tomorrow(self):
        self.edit_config(lambda c: c["friends"]["小王"].update(daily_cny=0.004))
        self.assertEqual(self.translate()[0], 200)
        self.assertEqual(self.translate()[0], 200)   # 0.0051 now, over the cap
        status, answer = self.translate()
        self.assertEqual(status, 402)
        self.assertEqual(answer["error"]["code"], "daily_quota")
        self.assertEqual(len(self.upstream.requests), 2)

    def test_total_cap_stops_everyone(self):
        other = self.add_friend("小李")
        self.edit_config(lambda c: c.update(total_daily_cny=0.002))
        self.assertEqual(self.translate()[0], 200)
        status, answer = self.post({"messages": [{"role": "user", "content": "x"}]}, code=other)
        self.assertEqual(status, 402)
        self.assertEqual(answer["error"]["code"], "total_quota")

    def test_too_many_requests_a_minute(self):
        self.edit_config(lambda c: c.update(requests_per_minute=2))
        self.assertEqual(self.translate()[0], 200)
        self.assertEqual(self.translate()[0], 200)
        self.assertEqual(self.translate()[1]["error"]["code"], "rate_limited")

    def test_removed_friend_and_new_code_take_effect_at_once(self):
        old = self.code
        from io import StringIO
        from contextlib import redirect_stdout
        out = StringIO()
        time.sleep(0.02)
        with redirect_stdout(out):
            self.cli("new-code", "小王")
        new = out.getvalue().strip().splitlines()[-1]
        self.assertEqual(self.post({"messages": [{"role": "user", "content": "x"}]}, code=old)[0], 401)
        self.assertEqual(self.post({"messages": [{"role": "user", "content": "x"}]}, code=new)[0], 200)
        time.sleep(0.02)
        with redirect_stdout(StringIO()):
            self.cli("remove-friend", "小王")
        self.assertEqual(self.post({"messages": [{"role": "user", "content": "x"}]}, code=new)[0], 401)

    def test_missing_key_makes_only_that_model_unavailable(self):
        time.sleep(0.02)
        tt_relay.write_json_atomic(self.keys_path, {"openai": FAKE_OPENAI_KEY, "deepseek": ""})
        status, answer = self.translate("deepseek-flash")
        self.assertEqual(status, 503)
        self.assertEqual(answer["error"]["code"], "model_unavailable")
        self.assertEqual(self.translate()[0], 200)

    def test_upstream_key_problem_is_not_blamed_on_the_friend(self):
        self.upstream.status = 401
        status, answer = self.translate()
        self.assertEqual(status, 502)
        self.assertEqual(answer["error"]["code"], "upstream_auth")
        self.assertFalse(os.path.exists(self.usage_path))

    def test_upstream_errors_are_passed_on(self):
        self.upstream.status = 429
        self.assertEqual(self.translate()[0], 429)

    def test_only_the_chat_path_is_served(self):
        self.assertEqual(self.post({"messages": []}, path="/v1/models")[0], 404)
        self.assertEqual(self.post({"model": "gpt-6-luna", "messages": [{"role": "user", "content": "x"}]},
                                   path="/chat/completions")[0], 200)
        with urllib.request.urlopen(self.url + "/healthz", timeout=5) as r:
            self.assertEqual(r.status, 200)

    def get(self, path, code=None):
        code = self.code if code is None else code
        req = urllib.request.Request(self.url + path, headers={"Authorization": f"Bearer {code}"} if code else {})
        try:
            with urllib.request.urlopen(req, timeout=10) as r:
                return r.status, json.loads(r.read())
        except urllib.error.HTTPError as e:
            return e.code, json.loads(e.read())

    def test_quota_shows_only_your_own_use_today(self):
        other = self.add_friend("小李", "--daily", "2")
        self.translate()
        status, mine = self.get("/v1/quota")
        self.assertEqual(status, 200)
        self.assertEqual(mine["date"], tt_relay.today())
        self.assertAlmostEqual(mine["used_cny"], 0.0026, places=4)
        self.assertEqual(mine["daily_cny"], 1.0)
        self.assertAlmostEqual(mine["remaining_cny"], 1.0 - 0.0026, places=4)
        self.assertEqual(mine["requests"], 1)
        self.assertEqual(mine["models"], ["gpt-6-luna", "deepseek-flash"])
        status, theirs = self.get("/v1/quota", code=other)
        self.assertEqual((theirs["used_cny"], theirs["daily_cny"], theirs["requests"]), (0.0, 2.0, 0))

    def test_quota_needs_a_valid_code_and_lists_only_models_with_a_key(self):
        self.assertEqual(self.get("/v1/quota", code="tt-wrong")[0], 401)
        self.assertEqual(self.get("/v1/quota", code="")[0], 401)
        time.sleep(0.02)
        tt_relay.write_json_atomic(self.keys_path, {"openai": FAKE_OPENAI_KEY, "deepseek": ""})
        self.assertEqual(self.get("/v1/quota")[1]["models"], ["gpt-6-luna"])

    def test_one_connection_carries_request_after_request(self):
        for _ in range(3):
            self.assertEqual(self.translate()[0], 200)
        ports = {r["port"] for r in self.upstream.requests}
        self.assertEqual(len(ports), 1)

    def test_a_connection_the_upstream_dropped_is_replaced_without_sending_twice(self):
        self.upstream.close_silently = True
        self.assertEqual(self.translate()[0], 200)
        time.sleep(0.1)
        self.upstream.close_silently = False
        self.assertEqual(self.translate()[0], 200)
        self.assertEqual(len(self.upstream.requests), 2)
        self.assertNotEqual(self.upstream.requests[0]["port"], self.upstream.requests[1]["port"])

    def test_unreachable_upstream_is_a_502(self):
        self.edit_config(lambda c: c["upstreams"]["openai"].update(url="http://127.0.0.1:9/v1/chat/completions"))
        status, answer = self.translate()
        self.assertEqual(status, 502)
        self.assertEqual(answer["error"]["code"], "upstream_unreachable")

    def test_no_messages_is_refused(self):
        self.assertEqual(self.post({"model": "gpt-6-luna", "messages": []})[0], 400)
        self.assertEqual(self.upstream.requests, [])

    def test_config_without_prices_does_not_start(self):
        self.edit_config(lambda c: c["models"]["gpt-6-luna"].pop("input_cny_per_m"))
        with self.assertRaises(SystemExit):
            tt_relay.Relay(self.config_path, self.keys_path, self.usage_path)


if __name__ == "__main__":
    unittest.main()
