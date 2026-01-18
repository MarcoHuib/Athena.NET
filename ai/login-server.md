# Login server migration prompt

Goal
- Achieve behavioral parity with legacy login server and stabilize the login/auth flow.

Current state (C#)
- Login server runs with Aspire SQL Edge and auto-migrate enabled.
- Config templates live in conf/templates; real conf is gitignored.
- Message catalog loading from conf/msg_conf/login_msg.conf is implemented.
- Case-insensitive usernames can be configured; SQL Server + MySQL supported.
- Password hashing/verification matches legacy (MD5 storage + encrypted client modes).
- Login DB defaults/indexes aligned with legacy (login/ipban/global_acc_reg/loginlog) and identity seed at 2000000.
- Auth node lifecycle/timeout handling matches legacy (AUTH_TIMEOUT + online cleanup).
- Login log messages and group restriction handling aligned with legacy defaults.
- IPBan cleanup behavior matches legacy when interval is disabled.
- Loginlog schema parity restored (no primary key, only IP index).
- Packet behavior parity reviewed and aligned (login refusal/notify paths, unblock time formatting).
- Subnet handling matches legacy mask/char_ip behavior.
- Web auth token generation/disable flow aligned with legacy (retries + delay).

Legacy references
- legacy/src/login/login.cpp
- legacy/src/login/loginclif.cpp
- legacy/src/login/loginchrif.cpp
- legacy/src/login/ipban.cpp
- legacy/src/login/loginlog.cpp
- legacy/conf/login_athena.conf
- legacy/conf/inter_athena.conf
- legacy/conf/subnet_athena.conf
- legacy/conf/msg_conf/login_msg.conf
- legacy/sql-files/main.sql
- legacy/sql-files/logs.sql

Open parity checks
 (none)

Next tasks
- Diff C# login flow against legacy login.cpp and loginclif.cpp behavior.
- Verify error messages and codes match login_msg.conf and legacy defaults.
- Add tests around auth node TTL and IPBan scheduling.

Definition of done
- Login success/failure behaviors and log output match legacy on same inputs.
- DB schema and migrations match legacy tables and constraints.
- Automated tests cover auth and ipban critical paths.

Cleanup notes
- Remove sections above as each parity check is completed.
