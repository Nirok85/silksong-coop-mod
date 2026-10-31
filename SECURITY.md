# Security Policy

## Supported Versions

| Version | Supported |
| --- | --- |
| 3.0.x | :white_check_mark: |
| < 3.0.0 | :x: |

## Threat Model

Silksong Co-op is a **trust-based peer-to-peer** mod. 

- **Handshake**: HMAC-SHA256 challenge-response using a room code as a pre-shared key. 
- **Post-handshake**: optional MAC authentication for datagrams when a session key is derived.
- **No global server**: connections go directly between peers (UDP) or via Steam P2P.

If you do not trust the person you are connecting to, assume they can influence shared game state.

## Best Practices

### Room Codes
- **Use room codes with secrets**. Prefer `SK-XXXX-XXXX-XXXX-YYYY-YYYY-YYYY-YYYY` (with the 80-bit secret). The legacy 12-character address-only code does **not** act as a secret.
- **Keep codes private**. Do not share room codes in public streams, screenshots, or chat logs.
- **Avoid direct IP:port joins** when possible. Direct joins have no shared secret barrier.
- **Use trusted networks**. Treat any authenticated peer as trusted.

### GitHub / Tokens
- If you ever accidentally exposed a Personal Access Token (PAT), **revoke it immediately** at [GitHub Settings → Tokens](https://github.com/settings/tokens).
- Tokens used for pushing must have minimal scopes. If pushing workflow changes, `workflow` scope is required, but avoid using long-lived tokens where possible.
- Never commit secrets, tokens, or private keys.

### Hosting
- Only forward UDP ports if you understand the implications.
- Be aware that exposing your IP address to untrusted peers carries normal P2P risks.

## Reporting a Vulnerability

If you believe you've found a security vulnerability:
1. **Do not** open a public issue with exploit details.
2. Email or privately message the maintainer with a clear description, steps to reproduce, and impact.
3. Allow reasonable time for a fix before public disclosure.

## Known Limitations (by design)

- **No host-authoritative arbitration**: any authenticated peer can send world/event/vitals/currency packets. The mod validates formats and uses idempotency, but does not vote or reject arbitrary actions from peers.
- **Direct IP joins are allowed**: for convenience, direct connections are possible without a room-code secret unless explicitly restricted.
- **Trust required**: there is no anti-cheat. Only connect to people you trust.
