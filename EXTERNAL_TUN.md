# External TUN experiment (Windows)

Branch: `experiment/external-tun`, 2026-11-03. This is a source-only prototype,
not a new mode in the shipped GUI. No server or installed app was changed.

## What the upstream sources establish

- The [macOS fork](https://github.com/ImRealDerli/Dagami-Agent-Proxy-macOS),
  reviewed at `eb78b3b`, has a separate Tokio/rustls transport. Useful ideas are
  a shared TLS context, bounded queues/deadlines, IPv4/IPv6 fallback, and one
  CONNECT retry before application bytes are sent. Never replay model requests
  after opening a tunnel. See its `docs/NETWORK_ENGINE.md`. Its helper is a
  separate process; adopting it in the Windows single-EXE app is separate work.
- [yuaotian/antigravity-proxy](https://github.com/yuaotian/antigravity-proxy)
  explicitly uses a side-loaded `version.dll`, injection and network hooks.
  It can avoid editing app files, but it is not free of in-process hooks.
  Its README also describes location failures despite a working SOCKS tunnel.
- [Clash Verge Rev](https://github.com/clash-verge-rev/clash-verge-rev) TUN
  provides an OS-level route. TUN alone is the route tested by this prototype.
  A listening mixed/SOCKS port does not prove TUN is enabled, the selected rule
  matches Google, or an account/model is available. Port 7897 is a suggestion;
  use the actual mixed/SOCKS port from the user's Clash configuration.

## Use

Install/configure Clash separately and enable its TUN mode. Close Antigravity
and use an unmodified installation and its actual settings/profile path.
This helper never installs software, changes DNS/routes, or enables TUN.

```powershell
python tools/external_tun.py doctor --port 7897
python tools/external_tun.py plan --port 7897 --exe 'C:\path\Antigravity.exe' --settings "$env:APPDATA\Antigravity\User\settings.json"
python tools/external_tun.py run --port 7897 --exe 'C:\path\Antigravity.exe' --settings "$env:APPDATA\Antigravity\User\settings.json"
```

`doctor` negotiates SOCKS5 only on loopback, with a timeout and no destination
or credentials. `plan` checks known conflicts without launching. `run` launches
the original EXE without Dagami's bridge, proxy env or Chromium proxy args.
Only the child's environment is cleaned; the parent's environment is untouched.
Existing app instances, known sidecar DLLs, patch/backup markers and proxy
settings are refused, not removed. Other user options are preserved or cause
an explicit conflict. Errors have exit code 2. The helper cannot prevent direct
traffic if the OS TUN route is disabled; use Clash routing/kill-switch controls.

Limits: this is not a binary authenticity check. Unknown patches, alternate
profiles, system proxy/PAC, registry settings and backend copies outside the
installation are not certified clean. Use the actual settings path. Socket
success does not establish that the application's traffic uses TUN. Compare
the destination in Clash connections, then test the app with your own account.
No model/account test was performed in this task. Existing AmneziaVPN was not
disabled and Clash was not installed alongside it for an unrequested route change.

Rollback: close the app launched by the helper; launch normally. The helper
writes no app/profile state. Restore/disable TUN through Clash if you enabled it.

## Endpoint substitution

The proposed `daily-cloudcode-pa.googleapis.com` to
`cloudcode-pa.googleapis.com` change is NOT enabled here. A successful unauthenticated
HTTP request does not prove compatible authenticated APIs or remove region policy.
The host strings differ in length. A raw replacement inside `app.asar` can break
ASAR offsets and integrity metadata. Correct work would need a version/signature
allowlist, structured archive rebuild, integrity handling, backup/hash verification
and a real authorized account check. Never pad the hostname or silently change
the endpoint in the external-TUN route. A DLL socket hook alone also cannot
rewrite an HTTPS hostname inside encrypted payloads safely.

## Codex in Dagami vs this running session

The native launcher passes `HTTP_PROXY`, `HTTPS_PROXY`, `ALL_PROXY` (both cases)
and loopback exclusions to the child, pointing to `http://127.0.0.1:<bridge port>`.
CLI launch also sets `NODE_USE_ENV_PROXY=1`. Chromium/desktop routing has a
separate launch/shortcut layer; proxy env alone does not cover every runtime.
The authenticated upstream belongs to Dagami's bridge, not to `.codex/.env`.
See `src/NativeCliApps.cs`, `src/ApplicationSupport.cs`, `src/AppProxyPolicy.cs`
and `src/DevProxyApp.cs`. Codex `config.toml` is not used for a made-up root proxy key.

At inspection, `.codex/.env` did not exist, proxy env was absent from the shell
(except NO_PROXY), Windows system proxy was disabled, AmneziaVPN's adapter was up,
and Codex backend sockets went to external port 443, not Dagami's loopback 53200.
This supports OS/VPN routing for those observed sockets; it does not establish
every route of every desktop component. No credentials or full process args are included in the report.

Validation: loopback fixtures cover split SOCKS replies, rejection, child-only env,
sidecar/patch/settings conflicts, dry-run, active-process refusal and mocked launch.
There is no claim of a live Clash/Antigravity end-to-end test.

Validation results for this branch: 131 Python tests completed (2 skipped), six
source-policy tests passed, staged source hygiene passed, source ZIP created,
and the native Windows app compiled. Seven of those Python tests target this
helper. The local port 7897 probe timed out; no live app was launched by it.
 Native fixture suites also passed: ApplicationSupportChecks (81 checks)
and CliChecks (45 checks), using loopback/temporary files.
