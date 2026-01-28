## Parity table
| Step | Athena.NET behavior | rAthena behavior | Match | Notes |
|---|---|---|---|---|
| 1 | If UseMd5Passwords && PasswordEnc != 0 then SendRefuseLoginAsync(3) | If use_md5_passwds and passwdenc md5 -> logclif_auth_failed(..., 3) | YES | Both reject md5-encoded login when server-side md5 is enabled. |
| 2 | If NewAccountFlag && AutoRegisterBaseId != null, replace UserId | New account creation via _M/_F suffix in login_mmo_auth | DIFF | Athena rewrites UserId from AutoRegisterBaseId; rAthena uses _M/_F suffix logic. |
| 3 | On !result.Success send RefuseLogin(error) and ApplyDynamicIpBanAsync on 0/1 | logclif_auth_failed + ipban_log when result 0/1 | PARTIAL | Both apply extra action on error 0/1; exact ban mechanism differs. |
| 4 | If isServer and (Sex != 2 or AccountId >= 5) reject; else RegisterCharServer + ack 0 | Char server connect requires sd->sex == 'S' and account_id < MAX_SERVERS; sends 0/3 | YES | Both enforce server-account constraints and send success/deny codes. |
| 5 | If GroupIdToConnect set and mismatch -> NotifyBan(1) | group_id_to_connect check -> logclif_sent_auth_result(fd,1) | YES | Same restriction with server closed code 1. |
| 6 | If MinGroupIdToConnect set and group too low -> NotifyBan(1) | min_group_id_to_connect check -> logclif_sent_auth_result(fd,1) | YES | Same restriction with server closed code 1. |
| 7 | If no char servers -> NotifyBan(1) | server_num == 0 -> logclif_sent_auth_result(fd,1) | YES | Both reject with server closed code 1. |
| 8 | If online user has CharServerId >= 0 -> kick, schedule disconnect, NotifyBan(8) | online data->char_server > -1 -> kick, timer, auth_result(8) | YES | Both kick and return error 8. |
| 9 | If online user CharServerId == -1 -> remove auth node + online user | data->char_server == -1 -> login_remove_auth_node + login_remove_online_user | YES | Both wipe prior session state. |
| 10 | AddAuthNode, AddOnlineUser(-1), ScheduleWaitingDisconnect, SendAcceptLoginAsync | login_add_auth_node, login_add_online_user, waiting_disconnect timer, send accept | YES | Auth node lifecycle aligns. |

## Concrete parity gaps
- Auto-register user id rewrite: Athena rewrites UserId from AutoRegisterBaseId; rAthena uses _M/_F suffix logic.
- Auth failure -> refuse + dynamic IP ban on error 0/1: Both apply extra action on error 0/1; exact ban mechanism differs.

## Evidence
### Step 1 evidence
- Athena.NET
```
/Users/marco/ai/athena-agent/athena-net/src/LoginServer/Net/ClientSession.cs
  331 |     {
  332 |         if (Config.UseMd5Passwords && request.PasswordEnc != 0)
  333 |         {
```
- rAthena
```
/Users/marco/ai/athena-agent/upstream/src/login/loginclif.cpp
  278 | 
  279 | 	if( login_config.use_md5_passwds ){
  280 | 		MD5_String( sd.passwd, sd.passwd );
```

### Step 2 evidence
- Athena.NET
```
/Users/marco/ai/athena-agent/athena-net/src/LoginServer/Net/ClientSession.cs
  337 | 
  338 |         if (Config.NewAccountFlag && request.AutoRegisterBaseId != null)
  339 |         {
```
- rAthena
```
/Users/marco/ai/athena-agent/upstream/src/login/login.cpp
  323 | 	// Account creation with _M/_F
  324 | 	if( login_config.new_account_flag ) {
  325 | 		if( len > 2 && strnlen(sd->passwd, NAME_LENGTH) > 0 && // valid user and password lengths
```

### Step 3 evidence
- Athena.NET
```
/Users/marco/ai/athena-agent/athena-net/src/LoginServer/Net/ClientSession.cs
  354 |             {
  355 |                 await SendRefuseLoginAsync(result.ErrorCode, result.UnblockTime, cancellationToken);
  356 |                 if (result.ErrorCode is 0 or 1)
```
- rAthena
```
/Users/marco/ai/athena-agent/upstream/src/login/loginclif.cpp
  221 | 
  222 | 	if( (result == 0 || result == 1) && login_config.dynamic_pass_failure_ban )
  223 | 		ipban_log(ip); // log failed password attempt
```

### Step 4 evidence
- Athena.NET
```
/Users/marco/ai/athena-agent/athena-net/src/LoginServer/Net/ClientSession.cs
  348 |             LoginLogger.Warning($"Login failed for user '{request.UserId}' from {remoteIp} (server={isServer}, code={result.ErrorCode}).");
  349 |             if (isServer)
  350 |             {
```
- rAthena
```
/Users/marco/ai/athena-agent/upstream/src/login/loginclif.cpp
  427 | 			result == -1 &&
  428 | 			sd->sex == 'S' &&
  429 | 			sd->account_id < ARRAYLENGTH(ch_server) &&
```

### Step 5 evidence
- Athena.NET
```
/Users/marco/ai/athena-agent/athena-net/src/LoginServer/Net/ClientSession.cs
  379 | 
  380 |         if (Config.GroupIdToConnect >= 0 && result.GroupId != Config.GroupIdToConnect)
  381 |         {
```
- rAthena
```
/Users/marco/ai/athena-agent/upstream/src/login/loginclif.cpp
   60 | 
   61 | 	if( login_config.group_id_to_connect >= 0 && sd->group_id != login_config.group_id_to_connect ) {
   62 | 		ShowStatus("Connection refused: the required group id for connection is %d (account: %s, group: %d).\n", login_config.group_id_to_connect, sd->userid, sd->group_id);
```

### Step 6 evidence
- Athena.NET
```
/Users/marco/ai/athena-agent/athena-net/src/LoginServer/Net/ClientSession.cs
  385 | 
  386 |         if (Config.MinGroupIdToConnect >= 0 && Config.GroupIdToConnect == -1 && result.GroupId < Config.MinGroupIdToConnect)
  387 |         {
```
- rAthena
```
/Users/marco/ai/athena-agent/upstream/src/login/loginclif.cpp
   64 | 		return;
   65 | 	} else if( login_config.min_group_id_to_connect >= 0 && login_config.group_id_to_connect == -1 && sd->group_id < login_config.min_group_id_to_connect ) {
   66 | 		ShowStatus("Connection refused: the minimum group id required for connection is %d (account: %s, group: %d).\n", login_config.min_group_id_to_connect, sd->userid, sd->group_id);
```

### Step 7 evidence
- Athena.NET
```
/Users/marco/ai/athena-agent/athena-net/src/LoginServer/Net/ClientSession.cs
  391 | 
  392 |         if (_charServers.Servers.Count == 0)
  393 |         {
```
- rAthena
```
/Users/marco/ai/athena-agent/upstream/src/login/loginclif.cpp
   75 | 
   76 | 	if( server_num == 0 )
   77 | 	{// if no char-server, don't send void list of servers, just disconnect the player with proper message
```

### Step 8 evidence
- Athena.NET
```
/Users/marco/ai/athena-agent/athena-net/src/LoginServer/Net/ClientSession.cs
  399 |         {
  400 |             if (existing.CharServerId >= 0)
  401 |             {
```
- rAthena
```
/Users/marco/ai/athena-agent/upstream/src/login/loginclif.cpp
   87 | 		{// account is already marked as online!
   88 | 			if( data->char_server > -1 )
   89 | 			{// Request char servers to kick this account out. [Skotlex]
```

### Step 9 evidence
- Athena.NET
```
/Users/marco/ai/athena-agent/athena-net/src/LoginServer/Net/ClientSession.cs
  407 | 
  408 |             if (existing.CharServerId == -1)
  409 |             {
```
- rAthena
```
/Users/marco/ai/athena-agent/upstream/src/login/loginclif.cpp
  100 | 			else
  101 | 			if( data->char_server == -1 )
  102 | 			{// client has authed but did not access char-server yet
```

### Step 10 evidence
- Athena.NET
```
/Users/marco/ai/athena-agent/athena-net/src/LoginServer/Net/ClientSession.cs
  414 | 
  415 |         _state.AddAuthNode(new AuthNode
  416 |         {
```
- rAthena
```
/Users/marco/ai/athena-agent/upstream/src/login/loginclif.cpp
  161 | 	// create temporary auth entry
  162 | 	login_add_auth_node( sd, ip );
  163 | 
```


## Auth parity table
| Step | Athena.NET behavior | rAthena behavior | Match | Notes |
|---|---|---|---|---|
| 1 | DB required: if db is null -> Fail(1) | Account DB accessed via login_get_accounts_db, login_mmo_auth returns error codes | PARTIAL | Both require account data; Athena hard-fails if DB factory returns null. |
| 2 | NewAccountFlag auto-register via TryAutoRegisterAsync | new_account_flag via _M/_F suffix in login_mmo_auth | DIFF | Auto-registration logic differs. |
| 3 | IP ban check (IsIpBannedAsync) when IpBanEnabled | ipban_check via login_config.ipban in logclif_parse | PARTIAL | Both enforce IP bans, but checks occur in different layers. |
| 4 | Reject if account not found -> Fail(0) | If accounts->load_str fails -> return 0 | YES | Both treat missing account as unregistered. |
| 5 | Reject server accounts when !isServer | Reject SEX_SERVER when !isServer | YES | Both prevent server accounts on client login. |
| 6 | Password check via CheckPassword | login_check_password validates passwdenc / md5 | PARTIAL | Both validate password; implementations differ. |
| 7 | Expiration/unban/state checks map to error codes | expiration_time/unban_time/state checks return codes 2/6/state-1 | YES | Both enforce expiry/unban/state. |
| 8 | Client hash enforcement (Config.ClientHashCheck) | client_hash_check in login_mmo_auth | PARTIAL | Both check client hash when enabled; verify exact config/paths. |
| 9 | On success: update login IDs, log login, update web auth token, update last login/ip | On success: update session data, update account data, set web_auth_token | YES | Success updates align at high level. |

## Concrete parity gaps
- Auto-registration flow differs (TryAutoRegisterAsync vs _M/_F suffix).
- IP ban check is in different layer (AuthenticateAsync vs logclif_parse).
- Password/hash validation logic differs in detail (CheckPassword vs login_check_password).

## Evidence
### Athena.NET AuthenticateAsync
```csharp
 1087 |     private async Task<AuthResult> AuthenticateAsync(LoginRequest request, string remoteIp, bool isServer, CancellationToken cancellationToken)
 1088 |     {
 1089 |         var db = _dbFactory();
 1090 |         if (db == null)
 1091 |         {
 1092 |             return AuthResult.Fail(1);
 1093 |         }
 1094 | 
 1095 |         await using (db)
 1096 |         {
 1097 |             if (!isServer && Config.IpBanEnabled)
 1098 |             {
 1099 |                 if (await IsIpBannedAsync(db, remoteIp, cancellationToken))
 1100 |                 {
 1101 |                     return AuthResult.Fail(3);
 1102 |                 }
 1103 |             }
 1104 | 
 1105 |             if (!isServer && Config.UseDnsbl && Config.DnsblServers.Length > 0)
 1106 |             {
 1107 |                 if (await IsDnsblListedAsync(remoteIp, cancellationToken))
 1108 |                 {
 1109 |                     await LogLoginAsync(db, request.UserId, remoteIp, 3, string.Empty, cancellationToken);
 1110 |                     return AuthResult.Fail(3);
 1111 |                 }
 1112 |             }
 1113 | 
 1114 |             if (!isServer && Config.NewAccountFlag)
 1115 |             {
 1116 |                 var regResult = await TryAutoRegisterAsync(db, request, remoteIp, cancellationToken);
 1117 |                 if (regResult.HasValue && regResult.Value != -1)
 1118 |                 {
 1119 |                     return AuthResult.Fail((uint)regResult.Value);
 1120 |                 }
 1121 |             }
 1122 | 
 1123 |             var userId = request.AutoRegisterBaseId ?? request.UserId;
 1124 | 
 1125 |             LoginAccount? account;
 1126 |             if (IsCaseSensitive)
 1127 |             {
 1128 |                 account = await db.Accounts
 1129 |                     .AsNoTracking()
 1130 |                     .FirstOrDefaultAsync(a => a.UserId == userId, cancellationToken);
 1131 |             }
 1132 |             else
 1133 |             {
 1134 |                 var normalizedUserId = userId.ToLowerInvariant();
 1135 |                 account = await db.Accounts
 1136 |                     .AsNoTracking()
 1137 |                     .FirstOrDefaultAsync(a => a.UserId.ToLower() == normalizedUserId, cancellationToken);
 1138 |             }
 1139 | 
 1140 |             if (account == null)
 1141 |             {
 1142 |                 await LogLoginAsync(db, userId, remoteIp, 0, string.Empty, cancellationToken);
 1143 |                 return AuthResult.Fail(0);
 1144 |             }
 1145 | 
 1146 |             if (!isServer && string.Equals(account.Sex, "S", StringComparison.OrdinalIgnoreCase))
 1147 |             {
 1148 |                 await LogLoginAsync(db, userId, remoteIp, 0, string.Empty, cancellationToken);
 1149 |                 return AuthResult.Fail(0);
 1150 |             }
 1151 | 
 1152 |             if (!CheckPassword(request, account))
 1153 |             {
 1154 |                 await LogLoginAsync(db, userId, remoteIp, 1, string.Empty, cancellationToken);
 1155 |                 return AuthResult.Fail(1);
 1156 |             }
 1157 | 
 1158 |             var now = DateTime.UtcNow;
 1159 |             if (account.ExpirationTime != 0 && account.ExpirationTime < ToUnixTime(now))
 1160 |             {
 1161 |                 await LogLoginAsync(db, userId, remoteIp, 2, string.Empty, cancellationToken);
 1162 |                 return AuthResult.Fail(2);
 1163 |             }
 1164 | 
 1165 |             if (account.UnbanTime != 0 && account.UnbanTime > ToUnixTime(now))
 1166 |             {
 1167 |                 var unblock = FormatDate(FromUnixTime(account.UnbanTime));
 1168 |                 await LogLoginAsync(db, userId, remoteIp, 6, string.Empty, cancellationToken);
 1169 |                 return AuthResult.Fail(6, unblock);
 1170 |             }
 1171 | 
 1172 |             if (account.State != 0)
 1173 |             {
 1174 |                 var error = (uint)Math.Max(0, (int)account.State - 1);
 1175 |                 await LogLoginAsync(db, userId, remoteIp, error, string.Empty, cancellationToken);
 1176 |                 return AuthResult.Fail(error);
 1177 |             }
 1178 | 
 1179 |             if (!isServer && Config.ClientHashCheck)
 1180 |             {
 1181 |                 if (!IsClientHashAllowed(account.GroupId))
 1182 |                 {
 1183 |                     await LogLoginAsync(db, userId, remoteIp, 5, string.Empty, cancellationToken);
 1184 |                     return AuthResult.Fail(5);
 1185 |                 }
 1186 |             }
 1187 | 
 1188 |         await UpdateAccountLoginAsync(db, account, remoteIp, cancellationToken);
 1189 |         await LogLoginAsync(db, userId, remoteIp, 100, "login ok", cancellationToken);
 1190 | 
 1191 |             var loginId1 = RandomNumberGenerator.GetInt32(1, int.MaxValue);
 1192 |             var loginId2 = RandomNumberGenerator.GetInt32(1, int.MaxValue);
 1193 | 
 1194 |             return AuthResult.FromAccount(account, loginId1, loginId2, remoteIp);
 1195 |         }
 1196 |     }
```
### rAthena login_mmo_auth
```cpp
  296 | int32 login_mmo_auth(struct login_session_data* sd, bool isServer) {
  297 | 	struct mmo_account acc;
  298 | 
  299 | 	char ip[16];
  300 | 	ip2str(session[sd->fd]->client_addr, ip);
  301 | 
  302 | 	// DNS Blacklist check
  303 | 	if( login_config.use_dnsbl ) {
  304 | 		char r_ip[16];
  305 | 		char ip_dnsbl[256];
  306 | 		char* dnsbl_serv;
  307 | 		uint8* sin_addr = (uint8*)&session[sd->fd]->client_addr;
  308 | 
  309 | 		sprintf(r_ip, "%u.%u.%u.%u", sin_addr[0], sin_addr[1], sin_addr[2], sin_addr[3]);
  310 | 
  311 | 		for( dnsbl_serv = strtok(login_config.dnsbl_servs,","); dnsbl_serv != nullptr; dnsbl_serv = strtok(nullptr,",") ) {
  312 | 			sprintf(ip_dnsbl, "%s.%s", r_ip, trim(dnsbl_serv));
  313 | 			if( host2ip(ip_dnsbl) ) {
  314 | 				ShowInfo("DNSBL: (%s) Blacklisted. User Kicked.\n", r_ip);
  315 | 				return 3;
  316 | 			}
  317 | 		}
  318 | 
  319 | 	}
  320 | 
  321 | 	size_t len = strnlen(sd->userid, NAME_LENGTH);
  322 | 
  323 | 	// Account creation with _M/_F
  324 | 	if( login_config.new_account_flag ) {
  325 | 		if( len > 2 && strnlen(sd->passwd, NAME_LENGTH) > 0 && // valid user and password lengths
  326 | 			sd->userid[len-2] == '_' && memchr("FfMm", sd->userid[len-1], 4) ) // _M/_F suffix
  327 | 		{
  328 | 			// Encoded password
  329 | 			if( sd->passwdenc != 0 ){
  330 | 				ShowError( "Account '%s' could not be created because client side password encryption is enabled.\n", sd->userid );
  331 | 				return 0; // unregistered id
  332 | 			}
  333 | 
  334 | 			int32 result;
  335 | 			// remove the _M/_F suffix
  336 | 			len -= 2;
  337 | 			sd->userid[len] = '\0';
  338 | 
  339 | 			result = login_mmo_auth_new(sd->userid, sd->passwd, TOUPPER(sd->userid[len+1]), ip);
  340 | 			if( result != -1 )
  341 | 				return result;// Failed to make account. [Skotlex].
  342 | 		}
  343 | 	}
  344 | 
  345 | 	if( !accounts->load_str(accounts, &acc, sd->userid) ) {
  346 | 		ShowNotice("Unknown account (account: %s, ip: %s)\n", sd->userid, ip);
  347 | 		return 0; // 0 = Unregistered ID
  348 | 	}
  349 | 
  350 | 	if( !isServer && sex_str2num( acc.sex ) == SEX_SERVER ){
  351 | 		ShowWarning( "Connection refused: ip %s tried to log into server account '%s'\n", ip, sd->userid );
  352 | 		return 0; // 0 = Unregistered ID
  353 | 	}
  354 | 
  355 | 	if( !login_check_password( *sd, acc ) ) {
  356 | 		ShowNotice("Invalid password (account: '%s', ip: %s)\n", sd->userid, ip);
  357 | 		return 1; // 1 = Incorrect Password
  358 | 	}
  359 | 
  360 | 	if( acc.expiration_time != 0 && acc.expiration_time < time(nullptr) ) {
  361 | 		ShowNotice("Connection refused (account: %s, expired ID, ip: %s)\n", sd->userid, ip);
  362 | 		return 2; // 2 = This ID is expired
  363 | 	}
  364 | 
  365 | 	if( acc.unban_time != 0 && acc.unban_time > time(nullptr) ) {
  366 | 		char tmpstr[24];
  367 | 		timestamp2string(tmpstr, sizeof(tmpstr), acc.unban_time, login_config.date_format);
  368 | 		ShowNotice("Connection refused (account: %s, banned until %s, ip: %s)\n", sd->userid, tmpstr, ip);
  369 | 		return 6; // 6 = Your are Prohibited to log in until %s
  370 | 	}
  371 | 
  372 | 	if( acc.state != 0 ) {
  373 | 		ShowNotice("Connection refused (account: %s, state: %d, ip: %s)\n", sd->userid, acc.state, ip);
  374 | 		return acc.state - 1;
  375 | 	}
  376 | 
  377 | 	if( login_config.client_hash_check && !isServer ) {
  378 | 		struct client_hash_node *node = nullptr;
  379 | 		bool match = false;
  380 | 
  381 | 		for( node = login_config.client_hash_nodes; node; node = node->next ) {
  382 | 			if( acc.group_id < node->group_id )
  383 | 				continue;
  384 | 			if( *node->hash == '\0' // Allowed to login without hash
  385 | 			 || (sd->has_client_hash && memcmp(node->hash, sd->client_hash, 16) == 0 ) // Correct hash
  386 | 			) {
  387 | 				match = true;
  388 | 				break;
  389 | 			}
  390 | 		}
  391 | 
  392 | 		if( !match ) {
  393 | 			char smd5[33];
  394 | 			int32 i;
  395 | 
  396 | 			if( !sd->has_client_hash ) {
  397 | 				ShowNotice("Client didn't send client hash (account: %s, ip: %s)\n", sd->userid, ip);
  398 | 				return 5;
  399 | 			}
  400 | 
  401 | 			for( i = 0; i < 16; i++ )
  402 | 				sprintf(&smd5[i * 2], "%02x", sd->client_hash[i]);
  403 | 
  404 | 			ShowNotice("Invalid client hash (account: %s, sent md5: %s, ip: %s)\n", sd->userid, smd5, ip);
  405 | 			return 5;
  406 | 		}
  407 | 	}
  408 | 
  409 | 	ShowNotice("Authentication accepted (account: %s, id: %d, ip: %s)\n", sd->userid, acc.account_id, ip);
  410 | 
  411 | 	// update session data
  412 | 	sd->account_id = acc.account_id;
  413 | 	sd->login_id1 = rnd_value(1u, UINT32_MAX);
  414 | 	sd->login_id2 = rnd_value(1u, UINT32_MAX);
  415 | 	safestrncpy(sd->lastlogin, acc.lastlogin, sizeof(sd->lastlogin));
  416 | 	sd->sex = acc.sex;
  417 | 	sd->group_id = acc.group_id;
  418 | 
  419 | 	// update account data
  420 | 	timestamp2string(acc.lastlogin, sizeof(acc.lastlogin), time(nullptr), "%Y-%m-%d %H:%M:%S");
  421 | 	safestrncpy(acc.last_ip, ip, sizeof(acc.last_ip));
  422 | 	acc.unban_time = 0;
  423 | 	acc.logincount++;
  424 | 	accounts->save(accounts, &acc, true);
  425 | 
  426 | 	if( login_config.use_web_auth_token ){
  427 | 		safestrncpy( sd->web_auth_token, acc.web_auth_token, WEB_AUTH_TOKEN_LENGTH );
  428 | 	}
  429 | 
  430 | 	if( sd->sex != 'S' && sd->account_id < START_ACCOUNT_NUM )
  431 | 		ShowWarning("Account %s has account id %d! Account IDs must be over %d to work properly!\n", sd->userid, sd->account_id, START_ACCOUNT_NUM);
  432 | 
  433 | 	return -1; // account OK
  434 | }
```
