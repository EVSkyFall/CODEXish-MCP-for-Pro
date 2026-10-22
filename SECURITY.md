# Security policy

CODEXish gives a remote ChatGPT session the power to read and change files, run commands and operate the desktop of
your PC with your Windows rights. Treat its URL, its password and your ChatGPT account as keys to that PC.

## Supported versions

Only the [latest release](https://github.com/EVSkyFall/CODEXish-MCP-for-Pro/releases/latest) receives fixes. `/healthz`
and `host_capabilities` report the running version.

## Reporting a vulnerability

Do not open a public issue, discussion or pull request.

- **Where to report.** Use **Report a vulnerability** on this repository's Security tab if it is offered. Otherwise
  contact the owner, [@EVSkyFall](https://github.com/EVSkyFall), privately on GitHub.
- **What to include.** The version, the steps to reproduce, what an attacker gains, and any logs. Redact your
  password, tokens and the client secret first.

This is a personal project. Reports are handled on a best-effort basis, without a response-time commitment.

## Security model

What CODEXish enforces, and what it does not.

- **Network exposure.**
  - The server listens on loopback only; your tunnel publishes it.
  - Requests whose `Host` is not the configured public host or loopback are refused, and so are browser requests
    from an unexpected `Origin`. These checks are not authentication.
  - The local control API answers only loopback connections with a loopback `Host` and the control token. It is never
    reachable through the tunnel.
- **Authentication.**
  - Every MCP request needs an OAuth bearer token issued by the built-in authorization server. A code is issued only
    after the CODEXish password; the server stores it as a PBKDF2-SHA256 hash.
  - Clients that register through `/register` are bound to the callbacks they registered. Public clients must use
    PKCE with S256.
  - Codes and tokens belong to the client that obtained them, and tokens are stored hashed.
  - The static client always needs its secret.
  - The sign-in page names the site it returns to.
- **Authorization.**
  - The file tools accept only paths inside a configured root with the matching Read or Write grant. A junction,
    symbolic link or other reparse point inside a root is refused, not followed.
  - Commands and processes start in a root with the Shell grant, but they run unconfined with your rights. So do Git
    hooks and mounted browser tools. Results say so with `execution_boundary: "unconfined_user"`.
  - There is no sandbox.
- **Data.**
  - Output is redacted with a small published pattern set, and redaction is not a guarantee. The tray's logs never
    contain the control token, the client secret or the password hash.
  - The ledger, artifacts and pre-edit backups live in `state_dir`. Retention removes them by age and touches only
    files CODEXish named.
- **Never done.** CODEXish never reads credential stores, never offers the clipboard as a tool, never elevates, and
  never acts on UAC prompts, the secure desktop or higher-integrity windows.
- **Your controls.** From the tray you can pause changes, stop the processes ChatGPT started, revoke all tokens,
  remove registered clients and stop the server.

## In scope

- Getting a token or a code without the CODEXish password.
- Using another client's code or token.
- Reaching MCP without a valid token, or the control API from anywhere but loopback.
- A file tool escaping its root or its grant.
- A secret that the documentation says is redacted appearing in output or logs.
- Retention deleting a file CODEXish did not create.
- Anything that lets content ChatGPT reads change CODEXish's own configuration, grants or tokens.

## Out of scope

- **Design-level access.** Anything a signed-in session can do through the shell, process or mounted tools; they run
  with your rights by design.
- **Compromise outside CODEXish.** A weak password, or a compromised PC, ChatGPT account or tunnel.
- **Model behavior.** Prompt injection that only changes what the model decides to do within the access it already
  has.
- **Code signing.** The release is not code-signed.

See the [README's safety model](README.md#safety-model) and the
[reference's limits and known gaps](docs/reference.md#limits-and-known-gaps).
