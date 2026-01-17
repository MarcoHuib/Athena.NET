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
