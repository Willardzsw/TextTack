# tt-relay 部署说明

TextTack 翻译转发服务：朋友用 TT码，服务器替他们调用 OpenAI 或 DeepSeek。Key 只放在服务器上，不会发到朋友的电脑。

## 服务器上的位置

| 内容 | 位置 | 权限 |
|---|---|---|
| 程序 | `/opt/tt-relay/tt_relay.py` | root |
| 配置（模型、单价、额度、TT码摘要） | `/etc/tt-relay/config.json` | root 可写，tt-relay 只读 |
| Key | `/etc/tt-relay/keys.json` | root 可写，tt-relay 只读 |
| 每天用量 | `/var/lib/tt-relay/usage.json` | tt-relay |
| 系统服务 | `tt-relay`，监听 `127.0.0.1:8230`，用专用账户 `tt-relay` 运行 | |
| Apache 网站 | `/etc/apache2/sites-available/tt-relay.conf`（和 certbot 生成的 `-le-ssl.conf`） | |

现有的 8 个网站配置都不改。

## 第一次部署

1. **在域名后台加 A 记录**，把子域名指向 `139.180.218.58`，等它生效。
2. **上传文件**（在这台电脑的 PowerShell 里运行）：

   ```powershell
   scp -r E:\Claude\overtranslate\tools\relay allplus-root:/root/tt-relay-upload
   ```

3. **运行部署脚本**：

   ```powershell
   ssh allplus-root "bash /root/tt-relay-upload/deploy.sh 你的子域名"
   ```

   脚本会：检查域名解析 → 备份 Apache 配置并记录现有网站状态 → 安装并启动转发程序 → 新增 Apache 网站 → 申请 HTTPS 证书 → 核对现有网站状态和部署前一致。

4. **填 Key**：由你自己登录服务器填写，不经过任何人：

   ```powershell
   ssh allplus-root
   ```

   然后在服务器上运行 `nano /etc/tt-relay/keys.json`，把两个新建的 Key（建议命名为 `tt-relay`）粘贴到引号里，按 Ctrl+O 保存，再按 Ctrl+X 退出。不用重启服务，下一次请求就会读取新的 Key。只填一个也可以，另一个模型会提示暂时不可用。

5. **添加朋友**（在服务器上）：

   ```
   python3 /opt/tt-relay/tt_relay.py add-friend 小王
   ```

   TT码只显示这一次，直接发给对方。服务器上只保存它的摘要，找不回原码；丢了就用 `new-code` 换一个新码。

## 日常管理（在服务器上以 root 运行）

| 要做的事 | 命令 |
|---|---|
| 看今天每个人用了多少 | `python3 /opt/tt-relay/tt_relay.py list` |
| 看最近 7 天用量 | `python3 /opt/tt-relay/tt_relay.py usage` |
| 添加朋友（可指定每天上限） | `python3 /opt/tt-relay/tt_relay.py add-friend 小李 --daily 2` |
| 改某人的每天上限 | `python3 /opt/tt-relay/tt_relay.py set-daily 小李 0.5` |
| 换码（旧码立即失效） | `python3 /opt/tt-relay/tt_relay.py new-code 小李` |
| 删除朋友 | `python3 /opt/tt-relay/tt_relay.py remove-friend 小李` |
| 看运行记录 | `journalctl -u tt-relay -n 50` |

修改后立即生效，不用重启。

## 配置项（`/etc/tt-relay/config.json`）

- `default_model`：模型名留空或不认识时使用，默认 `gpt-6-luna`。
- `models`：允许使用的模型，以及每百万 token 的单价（人民币）。默认按官网价格偏高估算：
  - Luna：输入 0.75、输出 3.6
  - DeepSeek Flash：按高峰价，输入 2、输出 8

  价格变了直接改这里。只用来算额度，和实际扣费无关。
- `default_daily_cny`：每个朋友每天的默认上限，¥1。
- `total_daily_cny`：所有朋友每天合计的上限，¥10，防止意外。
- `requests_per_minute`：每个朋友每分钟最多请求次数，30。
- `max_output_tokens`：每次回答最多 8192 token。

转发程序还会固定做这些事：
- 只转发翻译需要的字段；
- 去掉快速模式；
- 强制关闭思考；
- 不保存、不记录翻译内容。

## 更新程序

重新上传文件，再运行一次 `deploy.sh`。`config.json`、`keys.json` 和 Apache 网站配置都会保留。

## 回退

部署前的 Apache 配置备份在 `/root/tt-relay-backup-<时间>/apache2.tar.gz`。只撤掉转发服务的话：

```
a2dissite tt-relay tt-relay-le-ssl && apache2ctl configtest && systemctl reload apache2
systemctl disable --now tt-relay
rm /etc/systemd/system/tt-relay.service && systemctl daemon-reload
certbot delete --cert-name 你的子域名
```

`/etc/tt-relay`（含 Key）和 `/opt/tt-relay` 需要手动删除。

## 本地测试

```powershell
python -m unittest tools/relay/test_tt_relay.py -v
```

测试用的上游是本机上的假服务器，Key 和 TT码都是假值，不会连接 OpenAI 或 DeepSeek。
