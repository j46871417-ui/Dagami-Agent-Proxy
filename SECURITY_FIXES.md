# Security review fixes — 0.4.1 beta

The changes follow the review of commit `e04e5ed`. The release contains a Windows
build and source archive. Installed applications are updated only by the user.

| Finding | Implementation |
| --- | --- |
| Update helper can restart an unvalidated target | Restart is allowed only after a successful installation and a matching installed hash. The helper refuses elevation; protected installation folders require a manual update. |
| Missing/corrupt proxy configuration selects direct | Loading errors remain visible and block managed launch. An enabled proxy with an empty/invalid address is rejected. Direct routing and disabling the proxy require an explicit choice. |
| Conflicting Gemini proxy flags | Managed launch removes previous proxy/PAC/autodetect/no-proxy switches and builds one consistent configuration, preserving unrelated quoted arguments. |
| Live relays bypass the capacity limit | A native connection holds its slot until both relay directions complete. Python limits the complete handler/relay lifetime, not just upstream setup. |
| Repeated plain HTTP requests lose upstream authorization | Ordinary HTTP uses one framed transaction per connection with `Connection: close`. Every new connection receives the configured upstream authorization; pipelined requests cannot pass through unprocessed. CONNECT remains opaque and 101 upgrades retain duplex streaming. |
| Forged legacy licenses | A signed-license verifier and issuer contract are prepared. **Legacy acceptance intentionally remains enabled until the issuing bot is migrated. This finding is not closed yet.** See [SIGNED_LICENSES.md](SIGNED_LICENSES.md). |
| Other local accounts can use the proxy | The bridge identifies the client-side TCP owner and permits only its own OS account. Identity lookup failures deny access. Same-account processes remain trusted. |
| Damaged launchers displayed as patched | Invalid or unreadable launcher records display a recovery state and retain the undo action. They do not count as ready. |

## Gemini Desktop

When an external proxy is configured and enabled, Gemini gets a private session
bridge without Smart DNS. This applies the same external route to its proxied
login and chat requests even while the shared bridge uses mixed routing for
other applications. The session owns and closes this bridge. Logs identify the
selected bridge and `upstream-only` policy; starting an EXE is not reported as a
verified model response.

Completely close existing Gemini processes before managed launch. For WebView2,
use a normal user launch: elevated hosts can ignore environment overrides.
Applications that overwrite their own runtime proxy settings still require
runtime-specific support. The region refusal must be checked against a real
Gemini installation and the selected upstream; the local fixtures do not assert
that Google's account/region decision has changed.

## Compatibility and local trust

- Automatic in-place updates work in a normal user-writable installation.
  Running the updater as administrator is deliberately unsupported. For Program
  Files or other protected folders, replace the verified release manually and
  restart the GUI normally.
- Plain HTTP clients must reconnect after a response. Content-Length, chunked
  bodies/trailers, HEAD, informational responses, Expect: 100-continue and 101
  upgrades are handled separately from opaque CONNECT tunnels. Ambiguous framing
  is rejected instead of forwarding unparsed bytes.
- Windows client ownership uses GetExtendedTcpTable and the process token's
  user SID. Linux uses the client tuple's UID from `/proc/net/tcp*`; macOS uses
  the numeric socket owner reported by `/usr/sbin/lsof`. Unsupported platforms,
  unavailable identity information and other users are denied. Windows has live
  integration checks; Linux/macOS need native-platform integration verification
  before their release artifacts are published.
- This is isolation between OS accounts, not a sandbox between programs running
  as the same account. No firewall rules, extra accounts or privileges are
  installed by the ownership check.

## Regression checks

`SecurityReviewChecks` opens the actual native limit of live loopback tunnels,
checks 503 at saturation and recovery after close, authenticates reconnected HTTP
transactions, blocks pipelining, checks damaged state labels and verifies Gemini
bridge disposal. It generates temporary RSA keys to verify signatures, expiry,
product/issuer restrictions and rejection of other signing keys; the production
license store is untouched. Python adds live ownership, saturation, HTTP framing
and denied-identity tests. Existing ConnectRPC tests preserve chunked streaming,
trailers, application authorization and CSRF.
