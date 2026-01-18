# Configuration

Primary config files:
- `conf/login_athena.conf`
- `conf/inter_athena.conf`
- `conf/subnet_athena.conf`

Templates live in `conf/templates/` (copy into `conf/` to customize).

Notes
- `import:` lines are supported in the three config files (paths are relative to the file that declares them).
- Legacy aliases are supported for login config (e.g. `ipban_enable`, `ipban_dynamic_pass_failure_ban*`, `dnsbl_servers`).
- Login message text is loaded from `conf/msg_conf/login_msg.conf` (legacy format), override with `--login-msg-config <path>`.
- Console logging honors `console_msg_log`, `console_silent`, and `console_log_filepath` from `conf/login_athena.conf`.
- `timestamp_format` in `conf/login_athena.conf` prefixes console/file logs (legacy format tokens are supported).
- `login_case_sensitive` and `login_codepage` in `conf/inter_athena.conf` are supported. `login_codepage` is applied for MySQL connections only.
- Database credentials are not read from `conf/inter_athena.conf`; keep secrets in `solutionfiles/secrets/secret.json`.
