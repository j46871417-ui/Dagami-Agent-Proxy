# Signed license migration contract

Status: verifier prepared; issuing-bot migration pending. Per the owner's choice,
`SignedLicenses.AcceptLegacyKeys` remains `true`. Existing activations continue
working. Short legacy checksums remain forgeable while this compatibility policy
is active; this stage must not be described as a completed licensing fix.

The production issuer key is deliberately unconfigured. The client does not
generate a production private key, trust a key from a license, read a key from a
user-writable configuration file or accept a fixture signing key.

## Wire format

```
DAGAMI2.<issuer-key-id>.<payload-base64url>.<signature-base64url>
```

- The key ID is 1–32 lowercase ASCII letters, digits or hyphens, pinned together
  with the public key in `src/SignedLicenses.cs` at build time.
- Payload and signature use canonical unpadded base64url and are case-sensitive.
  The whole token is at most 4096 characters. The activation field preserves case.
- Signature: RSA SHA-256 with PKCS#1 v1.5 padding, using a 3072–8192-bit issuer key.
  Sign the ASCII bytes of `DAGAMI2.<issuer-key-id>.<payload-base64url>` exactly.
  Do not reserialize JSON during signature verification.
- The payload is UTF-8 JSON containing exactly these five fields:

| Field | Meaning |
| --- | --- |
| `v` | Integer `2` |
| `product` | String `dagami-agent-proxy` |
| `id` | Random license GUID, 32 hexadecimal digits without hyphens |
| `iat` | Positive integer Unix issuance time in seconds |
| `exp` | Integer Unix expiry time, or `0` for a perpetual license |

Future issuance beyond five minutes of clock tolerance is rejected. A nonzero
expiry must follow issuance and be later than the validation time. Do not embed
Telegram IDs, email addresses or other personal information in the token.

## Issuer rollout

1. Provision a production signing key in the bot's protected secret storage. Keep
   the private key out of Git, source ZIPs, public tooling, logs and the client.
2. Export **only** the RSA public parameters (Modulus and Exponent) as the .NET
   RSAKeyValue public XML. Pin that public key and its ID in the client build.
3. Implement the wire format above in the issuing bot. Verify its tokens against
   the native verifier, including altered payload/signature, incorrect product,
   incorrect key ID, expired token and untrusted-key cases.
4. Define how legitimate existing users obtain a replacement through the issuing
   bot. A legacy checksum alone is not proof that its holder obtained a key from
   the bot; use the bot's issuance records or another issuer-controlled process.
5. In the coordinated release, set `AcceptLegacyKeys=false`. Provide migration
   instructions before removing old acceptance. Stored legacy activation then
   requires a replacement signed key; do not silently convert an untrusted old
   checksum into a signed entitlement.

This offline scheme authenticates the issuer. Online revocation, binding to a
device and activation-count limits require a separate issuer/service design.
