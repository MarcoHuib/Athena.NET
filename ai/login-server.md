# Login server migration prompt

Goal
- Achieve behavioral parity with legacy login server and stabilize the login/auth flow.

Current state (C#)
- Login server runs with Aspire SQL Edge and auto-migrate enabled.
- Config templates live in conf/templates; real conf is gitignored.
- Message catalog loading from conf/msg_conf/login_msg.conf is implemented.
- Case-insensitive usernames can be configured; SQL Server + MySQL supported.

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
- Password hashing/verification: confirm exact scheme(s) (md5, salted, plaintext flags).
- Account registration/login flags, PIN code behavior, and login log fields.
- Auth node lifecycle and timeouts (AUTH_TIMEOUT, online_db, waiting_disconnect).
- IPBan behavior: schedule, cleanup, and table schema parity.
- Packet behavior: packet versions, edge cases for failed login/ban/expired.
- Subnet handling: masks and per-server IPs should match legacy behavior.
- Web auth token enable/disable on login/logout.

Next tasks
- Diff C# login flow against legacy login.cpp and loginclif.cpp behavior.
- Compare table schema defaults to legacy main.sql/logs.sql; fix mismatches.
- Verify error messages and codes match login_msg.conf and legacy defaults.
- Add tests around auth node TTL and IPBan scheduling.

Definition of done
- Login success/failure behaviors and log output match legacy on same inputs.
- DB schema and migrations match legacy tables and constraints.
- Automated tests cover auth and ipban critical paths.

Cleanup notes
- Remove sections above as each parity check is completed.
