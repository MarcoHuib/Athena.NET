## Parity table
| Step | Athena.NET behavior | rAthena behavior | Match | Notes |
|---|---|---|---|---|
| 1 | Parse connect packet, send auth request to login server, refuse if auth request fails | chclif_parse_reqtoconnect sets session, sends 0x2712 auth request or rejects if no login server | PARTIAL | Both request login-server auth; details/fields differ. |
| 2 | Select char: requires authenticated + pincode OK, map server available, character exists, then send map info | chclif_parse_charselect: checks map server, character exists, sets online, loads char, sends map info | PARTIAL | Both gate on server availability + character existence; Athena has pincode gating in method. |
| 3 | Create char: checks CharNew, validates name/job/slot, creates character, gives start items | chclif_parse_createnewchar: checks char_new, parses fields, calls char_make_new_char | PARTIAL | Both check creation flag and create character; Athena enforces extra validation and start items. |
| 4 | Delete char: deletes requested character (standard delete flow) | chclif_parse_delchar: handles delete requests | PARTIAL | Both implement delete; exact timing/conditions may differ. |
| 5 | Delete3 reserve/accept/cancel flows | chclif_parse_char_delete2_req/accept/cancel | PARTIAL | Both implement delete3 flows; check status codes and timing. |
| 6 | Pincode window/check/change/set flows handled in-session | chclif_parse_reqpincode_window/pincode_check/pincode_change/pincode_setnew | PARTIAL | Both implement pincode; Athena gating appears in HandleSelectCharAsync. |
| 7 | Rename check/apply via client packets | chclif_parse_reqrename/chclif_parse_ackrename | PARTIAL | Both support rename; verify validation rules. |
| 8 | Move character slot handling | chclif_parse_moveCharSlot | PARTIAL | Both support slot move; verify constraints and counters. |

## Concrete parity gaps
- Connect flow: Athena refuses when TrySendAuthRequest fails; rAthena rejects only if no login-server (different trigger).
- Select char: Athena enforces pincode gating inside HandleSelectCharAsync; rAthena handles pincode in separate handlers.
- Create char: Athena validates job/slot/name and allocates start items in method; rAthena delegates to char_make_new_char.
- Delete flows: verify delete result codes and timing (standard + delete3).
- Rename/pincode/slot-move: verify validation rules and failure codes.

## Evidence
### Athena.NET HandleConnectAsync
```csharp
  243 |     private async Task HandleConnectAsync(byte[] packet, CancellationToken cancellationToken)
  244 |     {
  245 |         if (packet.Length < 17)
  246 |         {
  247 |             return;
  248 |         }
  249 | 
  250 |         _accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
  251 |         _loginId1 = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(6, 4));
  252 |         _loginId2 = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(10, 4));
  253 |         _sex = packet[16];
  254 | 
  255 |         var buffer = new byte[4];
  256 |         BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), _accountId);
  257 |         await WriteAsync(buffer, cancellationToken);
  258 | 
  259 |         var remoteIp = (_client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.Loopback;
  260 |         if (!_loginConnector.TrySendAuthRequest(this, _accountId, _loginId1, _loginId2, _sex, remoteIp))
  261 |         {
  262 |             await SendRefuseEnterAsync(0, cancellationToken);
  263 |         }
  264 |     }
```
### rAthena chclif_parse_reqtoconnect
```cpp
  821 | int32 chclif_parse_reqtoconnect(int32 fd, struct char_session_data* sd,uint32 ipl){
  822 | 	if( RFIFOREST(fd) < 17 ) // request to connect
  823 | 		return 0;
  824 | 	else {
  825 | 		uint32 account_id = RFIFOL(fd,2);
  826 | 		uint32 login_id1 = RFIFOL(fd,6);
  827 | 		uint32 login_id2 = RFIFOL(fd,10);
  828 | 		int32 sex = RFIFOB(fd,16);
  829 | 		RFIFOSKIP(fd,17);
  830 | 
  831 | 		ShowInfo("request connect - account_id:%d/login_id1:%d/login_id2:%d\n", account_id, login_id1, login_id2);
  832 | 
  833 | 		if (sd) {
  834 | 			//Received again auth packet for already authentified account?? Discard it.
  835 | 			//TODO: Perhaps log this as a hack attempt?
  836 | 			//TODO: and perhaps send back a reply?
  837 | 			ShowInfo("Already registered break\n");
  838 | 			return 1;
  839 | 		}
  840 | 
  841 | 		CREATE(session[fd]->session_data, struct char_session_data, 1);
  842 | 		sd = (struct char_session_data*)session[fd]->session_data;
  843 | 		sd->account_id = account_id;
  844 | 		sd->login_id1 = login_id1;
  845 | 		sd->login_id2 = login_id2;
  846 | 		sd->sex = sex;
  847 | 		sd->auth = false; // not authed yet
  848 | 		sd->pincode_correct = false; // not entered pincode correctly yet
  849 | 
  850 | 		// send back account_id
  851 | 		WFIFOHEAD(fd,4);
  852 | 		WFIFOL(fd,0) = account_id;
  853 | 		WFIFOSET(fd,4);
  854 | 
  855 | 		if( !global_core->is_running() ){
  856 | 			chclif_reject(fd, 0); // rejected from server
  857 | 			return 1;
  858 | 		}
  859 | 
  860 | 		// search authentification
  861 | 		std::shared_ptr<struct auth_node> node = util::umap_find( char_get_authdb(), account_id);
  862 | 
  863 | 		if( node != nullptr &&
  864 | 			node->account_id == account_id &&
  865 | 			node->login_id1  == login_id1 &&
  866 | 			node->login_id2  == login_id2 /*&&
  867 | 			node->ip         == ipl*/ )
  868 | 		{// authentication found (coming from map server)
  869 | 			char_get_authdb().erase(account_id);
  870 | 			char_auth_ok(fd, sd);
  871 | 			sd->pincode_correct = true; // already entered pincode correctly yet
  872 | 		}
  873 | 		else
  874 | 		{// authentication not found (coming from login server)
  875 | 			if (session_isValid(login_fd)) { // don't send request if no login-server
  876 | 				WFIFOHEAD(login_fd,23);
  877 | 				WFIFOW(login_fd,0) = 0x2712; // ask login-server to authentify an account
  878 | 				WFIFOL(login_fd,2) = sd->account_id;
  879 | 				WFIFOL(login_fd,6) = sd->login_id1;
  880 | 				WFIFOL(login_fd,10) = sd->login_id2;
  881 | 				WFIFOB(login_fd,14) = sd->sex;
  882 | 				WFIFOL(login_fd,15) = htonl(ipl);
  883 | 				WFIFOL(login_fd,19) = fd;
  884 | 				WFIFOSET(login_fd,23);
  885 | 			} else { // if no login-server, we must refuse connection
  886 | 				chclif_reject(fd, 0); // rejected from server
  887 | 			}
  888 | 		}
  889 | 	}
  890 | 	return 1;
  891 | }
```
### Athena.NET HandleSelectCharAsync
```csharp
  266 |     private async Task HandleSelectCharAsync(byte[] packet, CancellationToken cancellationToken)
  267 |     {
  268 |         if (!_authenticated)
  269 |         {
  270 |             return;
  271 |         }
  272 |         if (packet.Length < 3)
  273 |         {
  274 |             return;
  275 |         }
  276 | 
  277 | 
  278 |         var config = _configStore.Current;
  279 |         if (config.PincodeEnabled && !string.IsNullOrEmpty(_pincode) && !_pincodeCorrect && !PincodePassed.ContainsKey(_accountId))
  280 |         {
  281 |             await SendRefuseEnterAsync(0, cancellationToken);
  282 |             return;
  283 |         }
  284 | 
  285 |         if (!_mapRegistry.TryGetAny(out var mapServer))
  286 |         {
  287 |             CharLogger.Warning("No map server available for character selection.");
  288 |             await SendRefuseEnterAsync(0, cancellationToken);
  289 |             return;
  290 |         }
  291 | 
  292 |         var slot = packet[2];
  293 |         var db = _dbFactory();
  294 |         if (db == null)
  295 |         {
  296 |             await SendRefuseEnterAsync(0, cancellationToken);
  297 |             return;
  298 |         }
  299 | 
  300 |         var character = await db.Characters.FirstOrDefaultAsync(c => c.AccountId == _accountId && c.CharNum == slot && c.DeleteDate == 0, cancellationToken);
  301 |         if (character == null)
  302 |         {
  303 |             await SendRefuseEnterAsync(0, cancellationToken);
  304 |             return;
  305 |         }
  306 | 
  307 |         var mapName = string.IsNullOrWhiteSpace(character.LastMap) ? character.SaveMap : character.LastMap;
  308 |         if (string.IsNullOrWhiteSpace(mapName))
  309 |         {
  310 |             mapName = "prontera";
  311 |         }
  312 | 
  313 |         var node = new MapAuthNode(
  314 |             _accountId,
  315 |             character.CharId,
  316 |             _loginId1,
  317 |             _loginId2,
  318 |             _sex,
  319 |             mapName,
  320 |             character.LastX,
  321 |             character.LastY,
  322 |             character.BodyDirection,
  323 |             character.Font,
  324 |             0,
  325 |             0,
  326 |             false);
  327 | 
  328 |         _mapAuthManager.Add(node);
  329 |         await SendZoneServerAsync(character.CharId, mapName, mapServer, cancellationToken);
  330 |     }
```
### rAthena chclif_parse_charselect
```cpp
 1074 | bool chclif_parse_charselect( int32 fd, struct char_session_data& sd ){
 1075 | 	const PACKET_CH_SELECT_CHAR* p = reinterpret_cast<PACKET_CH_SELECT_CHAR*>( RFIFOP( fd, 0 ) );
 1076 | 
 1077 | 	int32 server_id;
 1078 | 
 1079 | 	ARR_FIND( 0, ARRAYLENGTH(map_server), server_id, session_isValid(map_server[server_id].fd) && !map_server[server_id].maps.empty() );
 1080 | 	// Map-server not available, tell the client to wait (client wont close, char select will respawn)
 1081 | 	if (server_id == ARRAYLENGTH(map_server)) {
 1082 | 		chclif_accessible_maps( fd );
 1083 | 		return 1;
 1084 | 	}
 1085 | 
 1086 | 	// Check if the character exists and is not scheduled for deletion
 1087 | 	int slot = p->slot;
 1088 | 	char* data;
 1089 | 
 1090 | 	if ( SQL_SUCCESS != Sql_Query(sql_handle, "SELECT `char_id` FROM `%s` WHERE `account_id`='%d' AND `char_num`='%d' AND `delete_date` = 0", schema_config.char_db, sd.account_id, slot)
 1091 | 		|| SQL_SUCCESS != Sql_NextRow(sql_handle)
 1092 | 		|| SQL_SUCCESS != Sql_GetData(sql_handle, 0, &data, NULL) )
 1093 | 	{	//Not found?? May be forged packet.
 1094 | 		Sql_ShowDebug(sql_handle);
 1095 | 		Sql_FreeResult(sql_handle);
 1096 | 		chclif_reject(fd, 0); // rejected from server
 1097 | 		return 1;
 1098 | 	}
 1099 | 
 1100 | 	uint32 char_id = atoi(data);
 1101 | 	Sql_FreeResult(sql_handle);
 1102 | 
 1103 | 	// Prevent select a char while retrieving guild bound items
 1104 | 	if (sd.flag&1) {
 1105 | 		chclif_reject(fd, 0); // rejected from server
 1106 | 		return 1;
 1107 | 	}
 1108 | 
 1109 | 	/* client doesn't let it get to this point if you're banned, so its a forged packet */
 1110 | 	if( sd.found_char[slot] == char_id && sd.unban_time[slot] > time(nullptr) ) {
 1111 | 		chclif_reject(fd, 0); // rejected from server
 1112 | 		return 1;
 1113 | 	}
 1114 | 
 1115 | 	/* set char as online prior to loading its data so 3rd party applications will realise the sql data is not reliable */
 1116 | 	char_set_char_online(-2,char_id,sd.account_id);
 1117 | 
 1118 | 	struct mmo_charstatus char_dat;
 1119 | 	if( !char_mmo_char_fromsql(char_id, &char_dat, true) ) { /* failed? set it back offline */
 1120 | 		char_set_char_offline(char_id, sd.account_id);
 1121 | 		/* failed to load something. REJECT! */
 1122 | 		chclif_reject(fd, 0); // rejected from server
 1123 | 		return 1;
 1124 | 	}
 1125 | 
 1126 | 	//Have to switch over to the DB instance otherwise data won't propagate [Kevin]
 1127 | 	std::shared_ptr<struct mmo_charstatus> cd = util::umap_find( char_get_chardb(), char_id );
 1128 | 
 1129 | 	if (charserv_config.log_char) {
 1130 | 		char esc_name[NAME_LENGTH*2+1];
 1131 | 
 1132 | 		Sql_EscapeStringLen(sql_handle, esc_name, char_dat.name, strnlen(char_dat.name, NAME_LENGTH));
 1133 | 		if( SQL_ERROR == Sql_Query(sql_handle, "INSERT INTO `%s`(`time`, `account_id`,`char_num`,`name`) VALUES (NOW(), '%d', '%d', '%s')",
 1134 | 			schema_config.charlog_db, sd.account_id, slot, esc_name) )
 1135 | 			Sql_ShowDebug(sql_handle);
 1136 | 	}
 1137 | 	ShowInfo("Selected char: (Account %d: %d - %s)\n", sd.account_id, slot, char_dat.name);
 1138 | 
 1139 | 	// searching map server
 1140 | 	int i = char_search_mapserver( cd->last_point.map, -1, -1 );
 1141 | 
 1142 | 	// if map is not found, we check major cities
 1143 | 	if( i < 0 ){
 1144 | #if PACKETVER >= 20100714
 1145 | 		// Let the user select a map
 1146 | 		chclif_accessible_maps( fd );
 1147 | 
 1148 | 		return 0;
 1149 | #else
 1150 | 		// Try to select a map for the user
 1151 | 		uint16 j;
 1152 | 		//First check that there's actually a map server online.
 1153 | 		ARR_FIND( 0, ARRAYLENGTH(map_server), j, session_isValid(map_server[j].fd) && !map_server[j].maps.empty() );
 1154 | 		if (j == ARRAYLENGTH(map_server)) {
 1155 | 			ShowInfo("Connection Closed. No map servers available.\n");
 1156 | 			chclif_send_auth_result(fd,1); // 01 = Server closed
 1157 | 			return 1;
 1158 | 		}
 1159 | 
 1160 | 		for( struct s_point_str& accessible_map : accessible_maps ){
 1161 | 			i = char_search_mapserver( accessible_map.map, -1, -1 );
 1162 | 
 1163 | 			// Found a map-server for a map
 1164 | 			if( i >= 0 ){
 1165 | 				ShowWarning( "Unable to find map-server for '%s', sending to major city '%s'.\n", cd->last_point.map, accessible_map.map );
 1166 | 				memcpy( &cd->last_point, &accessible_map, sizeof( cd->last_point ) );
 1167 | 				break;
 1168 | 			}
 1169 | 		}
 1170 | 
 1171 | 		if( i < 0 ){
 1172 | 			ShowInfo( "Connection Closed. No map server available that has a major city, and unable to find map-server for '%s'.\n", cd->last_point.map );
 1173 | 			chclif_send_auth_result(fd,1); // 01 = Server closed
 1174 | 			return 1;
 1175 | 		}
 1176 | #endif
 1177 | 	}
 1178 | 
 1179 | 	//Send NEW auth packet [Kevin]
 1180 | 	//FIXME: is this case even possible? [ultramage]
 1181 | 	if( !session_isValid( map_server[i].fd ) ){
 1182 | 		ShowError( "parse_char: Attempting to write to invalid session %d! Map Server #%d disconnected.\n", map_server[i].fd, i );
 1183 | 		map_server[i] = {};
 1184 | 		map_server[i].fd = -1;
 1185 | 		chclif_send_auth_result(fd,1);  //Send server closed.
 1186 | 		return 1;
 1187 | 	}
 1188 | 
 1189 | 	chclif_send_map_data( fd, cd, i );
 1190 | 
 1191 | 	// create temporary auth entry
 1192 | 	std::shared_ptr<struct auth_node> node = std::make_shared<struct auth_node>();
 1193 | 
 1194 | 	node->account_id = sd.account_id;
 1195 | 	node->char_id = cd->char_id;
 1196 | 	node->login_id1 = sd.login_id1;
 1197 | 	node->login_id2 = sd.login_id2;
 1198 | 	node->sex = sd.sex;
 1199 | 	node->expiration_time = sd.expiration_time;
 1200 | 	node->group_id = sd.group_id;
 1201 | 	node->ip = session[fd]->client_addr;
 1202 | 
 1203 | 	char_get_authdb()[node->account_id] = node;
 1204 | 
 1205 | 	return 1;
 1206 | }
```
### Athena.NET HandleMakeCharAsync
```csharp
  451 |     private async Task HandleMakeCharAsync(byte[] packet, CancellationToken cancellationToken)
  452 |     {
  453 |         if (!_authenticated)
  454 |         {
  455 |             await SendRefuseMakeCharAsync(cancellationToken);
  456 |             return;
  457 |         }
  458 | 
  459 |         if (packet.Length < 36)
  460 |         {
  461 |             await SendRefuseMakeCharAsync(cancellationToken);
  462 |             return;
  463 |         }
  464 | 
  465 |         var config = _configStore.Current;
  466 |         if (!config.CharNew)
  467 |         {
  468 |             await SendRefuseMakeCharAsync(cancellationToken);
  469 |             return;
  470 |         }
  471 | 
  472 |         var name = NormalizeName(ReadFixedString(packet.AsSpan(2, 24)));
  473 |         var slot = packet[26];
  474 |         var hairColor = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(27, 2));
  475 |         var hairStyle = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(29, 2));
  476 |         var job = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(31, 4));
  477 |         var sex = packet[35];
  478 | 
  479 |         if (string.IsNullOrWhiteSpace(name) || slot >= _charSlots)
  480 |         {
  481 |             await SendRefuseMakeCharAsync(cancellationToken);
  482 |             return;
  483 |         }
  484 | 
  485 |         if (job != 0 && job != JobSummoner && job != JobBabySummoner)
  486 |         {
  487 |             await SendRefuseMakeCharAsync(cancellationToken);
  488 |             return;
  489 |         }
  490 | 
  491 |         var nameValidation = await ValidateCharNameAsync(name, cancellationToken);
  492 |         if (nameValidation != NameValidationResult.Ok)
  493 |         {
  494 |             await SendRefuseMakeCharAsync(cancellationToken);
  495 |             return;
  496 |         }
  497 | 
  498 |         var db = _dbFactory();
  499 |         if (db == null)
  500 |         {
  501 |             await SendRefuseMakeCharAsync(cancellationToken);
  502 |             return;
  503 |         }
  504 | 
  505 |         await using (db)
  506 |         {
  507 |             var accountCount = await db.Characters.CountAsync(c => c.AccountId == _accountId, cancellationToken);
  508 |             if (accountCount >= _charSlots)
  509 |             {
  510 |                 await SendRefuseMakeCharAsync(cancellationToken);
  511 |                 return;
  512 |             }
  513 | 
  514 |             var slotTaken = await db.Characters.AnyAsync(c => c.AccountId == _accountId && c.CharNum == slot, cancellationToken);
  515 |             if (slotTaken)
  516 |             {
  517 |                 await SendRefuseMakeCharAsync(cancellationToken);
  518 |                 return;
  519 |             }
  520 | 
  521 |             var startPoint = SelectStartPoint(config, job);
  522 |             var vit = 1;
  523 |             var intStat = 1;
  524 |             var maxHp = (uint)(40 * (100 + vit) / 100);
  525 |             var maxSp = (uint)(11 * (100 + intStat) / 100);
  526 |             var character = new CharCharacter
  527 |             {
  528 |                 AccountId = _accountId,
  529 |                 CharNum = slot,
  530 |                 Name = name,
  531 |                 Class = (ushort)Math.Clamp((int)job, 0, ushort.MaxValue),
  532 |                 BaseLevel = 1,
  533 |                 JobLevel = 1,
  534 |                 BaseExp = 0,
  535 |                 JobExp = 0,
  536 |                 Zeny = (uint)Math.Clamp(config.StartZeny, 0, int.MaxValue),
  537 |                 Str = 1,
  538 |                 Agi = 1,
  539 |                 Vit = (ushort)vit,
  540 |                 Int = (ushort)intStat,
  541 |                 Dex = 1,
  542 |                 Luk = 1,
  543 |                 Pow = 0,
  544 |                 Sta = 0,
  545 |                 Wis = 0,
  546 |                 Spl = 0,
  547 |                 Con = 0,
  548 |                 Crt = 0,
  549 |                 MaxHp = maxHp,
  550 |                 Hp = maxHp,
  551 |                 MaxSp = maxSp,
  552 |                 Sp = maxSp,
  553 |                 MaxAp = 0,
  554 |                 Ap = 0,
  555 |                 StatusPoint = (uint)Math.Max(0, _startStatusPoints),
  556 |                 SkillPoint = 0,
  557 |                 TraitPoint = 0,
  558 |                 Option = 0,
  559 |                 Karma = 0,
  560 |                 Manner = 0,
  561 |                 PartyId = 0,
  562 |                 GuildId = 0,
  563 |                 PetId = 0,
  564 |                 HomunId = 0,
  565 |                 ElementalId = 0,
  566 |                 Hair = (byte)Math.Clamp((int)hairStyle, 0, byte.MaxValue),
  567 |                 HairColor = hairColor,
  568 |                 ClothesColor = 0,
  569 |                 Body = 0,
  570 |                 Weapon = 0,
  571 |                 Shield = 0,
  572 |                 HeadTop = 0,
  573 |                 HeadMid = 0,
  574 |                 HeadBottom = 0,
  575 |                 Robe = 0,
  576 |                 LastMap = startPoint.Map,
  577 |                 LastX = startPoint.X,
  578 |                 LastY = startPoint.Y,
  579 |                 LastInstanceId = 0,
  580 |                 SaveMap = startPoint.Map,
  581 |                 SaveX = startPoint.X,
  582 |                 SaveY = startPoint.Y,
  583 |                 PartnerId = 0,
  584 |                 Online = 0,
  585 |                 Father = 0,
  586 |                 Mother = 0,
  587 |                 Child = 0,
  588 |                 Fame = 0,
  589 |                 Rename = 0,
  590 |                 DeleteDate = 0,
  591 |                 Moves = 0,
  592 |                 UnbanTime = 0,
  593 |                 Font = 0,
  594 |                 UniqueItemCounter = 0,
  595 |                 Sex = (sex == 0 || sex == 1) ? (sex == 0 ? "F" : "M") : (_sex == 0 ? "F" : "M"),
  596 |                 HotkeyRowShift = 0,
  597 |                 HotkeyRowShift2 = 0,
  598 |                 ClanId = 0,
  599 |                 LastLogin = null,
  600 |                 TitleId = 0,
  601 |                 ShowEquip = 0,
  602 |                 InventorySlots = 100,
  603 |                 BodyDirection = 0,
  604 |                 DisableCall = 0,
  605 |                 DisablePartyInvite = 0,
  606 |                 DisableShowCostumes = 0,
  607 |             };
  608 | 
  609 |             db.Characters.Add(character);
  610 |             await db.SaveChangesAsync(cancellationToken);
  611 | 
  612 |             var items = SelectStartItems(config, job)
  613 |                 .Select(item => new CharInventory
  614 |                 {
  615 |                     CharId = character.CharId,
  616 |                     NameId = item.ItemId,
  617 |                     Amount = item.Amount,
  618 |                     Equip = item.EquipPosition,
  619 |                     Identify = 1,
  620 |                     Refine = 0,
  621 |                     Attribute = 0,
  622 |                     Card0 = 0,
  623 |                     Card1 = 0,
  624 |                     Card2 = 0,
  625 |                     Card3 = 0,
  626 |                     OptionId0 = 0,
  627 |                     OptionVal0 = 0,
  628 |                     OptionParm0 = 0,
  629 |                     OptionId1 = 0,
  630 |                     OptionVal1 = 0,
  631 |                     OptionParm1 = 0,
  632 |                     OptionId2 = 0,
  633 |                     OptionVal2 = 0,
  634 |                     OptionParm2 = 0,
  635 |                     OptionId3 = 0,
  636 |                     OptionVal3 = 0,
  637 |                     OptionParm3 = 0,
  638 |                     OptionId4 = 0,
  639 |                     OptionVal4 = 0,
  640 |                     OptionParm4 = 0,
  641 |                     ExpireTime = 0,
  642 |                     Favorite = 0,
  643 |                     Bound = 0,
  644 |                     UniqueId = 0,
  645 |                     EquipSwitch = 0,
  646 |                     EnchantGrade = 0,
  647 |                 })
  648 |                 .ToList();
  649 | 
  650 |             if (items.Count > 0)
  651 |             {
  652 |                 db.Inventory.AddRange(items);
  653 |                 await db.SaveChangesAsync(cancellationToken);
  654 |             }
  655 | 
  656 |             await SendAcceptMakeCharAsync(character, cancellationToken);
  657 |         }
  658 |     }
```
### rAthena chclif_parse_createnewchar
```cpp
 1250 | bool chclif_parse_createnewchar( int32 fd, struct char_session_data& sd ){
 1251 | 	// Check if character creation is turned off
 1252 | 	if( charserv_config.char_new == 0 ){ 
 1253 | 		chclif_createnewchar_refuse( fd, -2 );
 1254 | 		return true;
 1255 | 	}
 1256 | 
 1257 | 	char name[NAME_LENGTH];
 1258 | 	int32 str, agi, vit, int_, dex, luk;
 1259 | 	int32 slot;
 1260 | 	int32 hair_color;
 1261 | 	int32 hair_style;
 1262 | 	int32 start_job;
 1263 | 	int32 sex;
 1264 | 
 1265 | 	const PACKET_CH_MAKE_CHAR* p = reinterpret_cast<PACKET_CH_MAKE_CHAR*>( RFIFOP( fd, 0 ) );
 1266 | 
 1267 | 	// Sent values
 1268 | 	safestrncpy( name, p->name, NAME_LENGTH );
 1269 | 	slot = p->slot;
 1270 | 	hair_color = p->hair_color;
 1271 | 	hair_style = p->hair_style;
 1272 | 
 1273 | #if PACKETVER >= 20151001
 1274 | 	// Sent values
 1275 | 	start_job = p->job;
 1276 | 	sex = p->sex;
 1277 | 
 1278 | 	// Default values
 1279 | 	str = 1;
 1280 | 	agi = 1;
 1281 | 	vit = 1;
 1282 | 	int_ = 1;
 1283 | 	dex = 1;
 1284 | 	luk = 1;
 1285 | #elif PACKETVER >= 20120307
 1286 | 	// Default values
 1287 | 	str = 1;
 1288 | 	agi = 1;
 1289 | 	vit = 1;
 1290 | 	int_ = 1;
 1291 | 	dex = 1;
 1292 | 	luk = 1;
 1293 | 	start_job = JOB_NOVICE;
 1294 | 	sex = sd.sex;
 1295 | #else
 1296 | 	// Sent values
 1297 | 	str = p->str;
 1298 | 	agi = p->agi;
 1299 | 	vit = p->vit;
 1300 | 	int_ = p->int_;
 1301 | 	dex = p->dex;
 1302 | 	luk = p->luk;
 1303 | 
 1304 | 	// Default values
 1305 | 	start_job = JOB_NOVICE;
 1306 | 	sex = sd.sex;
 1307 | #endif
 1308 | 
 1309 | 	int char_id = char_make_new_char( &sd, name, str, agi, vit, int_, dex, luk, slot, hair_color, hair_style, start_job, sex );
 1310 | 
 1311 | 	if( char_id < 0 ){
 1312 | 		chclif_createnewchar_refuse( fd, char_id );
 1313 | 		return true;
 1314 | 	}
 1315 | 
 1316 | 	// retrieve data
 1317 | 	struct mmo_charstatus char_dat;
 1318 | 
 1319 | 	// Only the short data is needed.
 1320 | 	char_mmo_char_fromsql( char_id, &char_dat, false );
 1321 | 
 1322 | 	chclif_createnewchar( fd, char_dat );
 1323 | 
 1324 | 	// add new entry to the chars list
 1325 | 	sd.found_char[char_dat.slot] = char_id;
 1326 | 
 1327 | 	return true;
 1328 | }
```
### Athena.NET HandleDeleteCharAsync
```csharp
  660 |     private async Task HandleDeleteCharAsync(byte[] packet, CancellationToken cancellationToken)
  661 |     {
  662 |         if (!_authenticated)
  663 |         {
  664 |             await SendRefuseDeleteCharAsync(cancellationToken);
  665 |             return;
  666 |         }
  667 | 
  668 |         if (packet.Length < 56)
  669 |         {
  670 |             await SendRefuseDeleteCharAsync(cancellationToken);
  671 |             return;
  672 |         }
  673 | 
  674 |         var charId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
  675 |         var code = ReadFixedString(packet.AsSpan(6, 50));
  676 |         var config = _configStore.Current;
  677 | 
  678 |         if (!IsDeleteCodeValid(code, config.CharDeleteOption))
  679 |         {
  680 |             await SendRefuseDeleteCharAsync(cancellationToken);
  681 |             return;
  682 |         }
  683 | 
  684 |         var db = _dbFactory();
  685 |         if (db == null)
  686 |         {
  687 |             await SendRefuseDeleteCharAsync(cancellationToken);
  688 |             return;
  689 |         }
  690 | 
  691 |         await using (db)
  692 |         {
  693 |             var character = await db.Characters
  694 |                 .FirstOrDefaultAsync(c => c.CharId == charId && c.AccountId == _accountId, cancellationToken);
  695 |             if (character == null || IsDeleteRestricted(config, character))
  696 |             {
  697 |                 await SendRefuseDeleteCharAsync(cancellationToken);
  698 |                 return;
  699 |             }
  700 | 
  701 |             var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
  702 |             if (config.CharDeleteDelaySeconds > 0 && (character.DeleteDate == 0 || character.DeleteDate > now))
  703 |             {
  704 |                 await SendRefuseDeleteCharAsync(cancellationToken);
  705 |                 return;
  706 |             }
  707 | 
  708 |             db.Characters.Remove(character);
  709 |             await db.SaveChangesAsync(cancellationToken);
  710 |         }
  711 | 
  712 |         var buffer = new byte[2];
  713 |         BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.HcAcceptDeleteChar);
  714 |         await WriteAsync(buffer, cancellationToken);
  715 |     }
```
### rAthena chclif_parse_delchar
```cpp
 1356 | bool chclif_parse_delchar( int fd, struct char_session_data& sd ){
 1357 | 	const PACKET_CH_DELETE_CHAR* p = reinterpret_cast<PACKET_CH_DELETE_CHAR*>( RFIFOP( fd, 0 ) );
 1358 | 
 1359 | 	char email[40];
 1360 | 	uint32 cid = p->CID;
 1361 | 
 1362 | 	ShowInfo(CL_RED "Request Char Deletion: " CL_GREEN "%u (%u)" CL_RESET "\n", sd.account_id, cid);
 1363 | 	safestrncpy( email, p->key, sizeof( email ) );
 1364 | 
 1365 | 	if (!chclif_delchar_check(&sd, email, charserv_config.char_config.char_del_option)) {
 1366 | 		chclif_refuse_delchar(fd,0); // 00 = Incorrect Email address
 1367 | 		return true;
 1368 | 	}
 1369 | 
 1370 | 	/* Delete character */
 1371 | 	switch( char_delete(&sd,cid) ){
 1372 | 		case CHAR_DELETE_OK:
 1373 | 			// Char successfully deleted.
 1374 | 			chclif_delchar( fd );
 1375 | 			break;
 1376 | 		case CHAR_DELETE_DATABASE:
 1377 | 		case CHAR_DELETE_BASELEVEL:
 1378 | 		case CHAR_DELETE_TIME:
 1379 | 			chclif_refuse_delchar(fd, 0);
 1380 | 			break;
 1381 | 		case CHAR_DELETE_NOTFOUND:
 1382 | 			chclif_refuse_delchar(fd, 1);
 1383 | 			break;
 1384 | 		case CHAR_DELETE_GUILD:
 1385 | 		case CHAR_DELETE_PARTY:
 1386 | 			chclif_refuse_delchar(fd, 2);
 1387 | 			break;
 1388 | 	}
 1389 | 
 1390 | 	return true;
 1391 | }
```
### Athena.NET HandleDeleteChar3ReserveAsync
```csharp
  717 |     private async Task HandleDeleteChar3ReserveAsync(byte[] packet, CancellationToken cancellationToken)
  718 |     {
  719 |         if (!_authenticated)
  720 |         {
  721 |             await SendDeleteChar3ReservedAsync(0, 3, 0, cancellationToken);
  722 |             return;
  723 |         }
  724 | 
  725 |         if (packet.Length < 6)
  726 |         {
  727 |             await SendDeleteChar3ReservedAsync(0, 3, 0, cancellationToken);
  728 |             return;
  729 |         }
  730 | 
  731 |         var charId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
  732 |         var config = _configStore.Current;
  733 |         var db = _dbFactory();
  734 |         if (db == null)
  735 |         {
  736 |             await SendDeleteChar3ReservedAsync(charId, 3, 0, cancellationToken);
  737 |             return;
  738 |         }
  739 | 
  740 |         var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
  741 |         await using (db)
  742 |         {
  743 |             var character = await db.Characters
  744 |                 .FirstOrDefaultAsync(c => c.CharId == charId && c.AccountId == _accountId, cancellationToken);
  745 |             if (character == null)
  746 |             {
  747 |                 await SendDeleteChar3ReservedAsync(charId, 3, 0, cancellationToken);
  748 |                 return;
  749 |             }
  750 | 
  751 |             if ((config.CharDeleteRestriction & CharDelRestrictGuild) != 0 && character.GuildId != 0)
  752 |             {
  753 |                 await SendDeleteChar3ReservedAsync(charId, 4, 0, cancellationToken);
  754 |                 return;
  755 |             }
  756 | 
  757 |             if ((config.CharDeleteRestriction & CharDelRestrictParty) != 0 && character.PartyId != 0)
  758 |             {
  759 |                 await SendDeleteChar3ReservedAsync(charId, 5, 0, cancellationToken);
  760 |                 return;
  761 |             }
  762 | 
  763 |             if (IsDeleteLevelBlocked(config.CharDeleteLevel, character.BaseLevel))
  764 |             {
  765 |                 await SendDeleteChar3ReservedAsync(charId, 0, 0, cancellationToken);
  766 |                 return;
  767 |             }
  768 | 
  769 |             var deleteDate = (uint)DateTimeOffset.UtcNow.AddSeconds(config.CharDeleteDelaySeconds).ToUnixTimeSeconds();
  770 |             character.DeleteDate = deleteDate;
  771 |             await db.SaveChangesAsync(cancellationToken);
  772 | 
  773 |             var remaining = deleteDate > now ? (uint)(deleteDate - now) : 0u;
  774 |             await SendDeleteChar3ReservedAsync(charId, 1, remaining, cancellationToken);
  775 |         }
  776 |     }
```
### rAthena chclif_parse_char_delete2_req
```cpp
  588 | bool chclif_parse_char_delete2_req( int32 fd, char_session_data& sd ){
  589 | 	const PACKET_CH_DELETE_CHAR3_RESERVED* p = reinterpret_cast<PACKET_CH_DELETE_CHAR3_RESERVED*>( RFIFOP( fd, 0 ) );
  590 | 
  591 | 	uint32 char_id = p->CID;
  592 | 	size_t i;
  593 | 
  594 | 	ARR_FIND( 0, MAX_CHARS, i, sd.found_char[i] == char_id );
  595 | 
  596 | 	// character not found
  597 | 	if( i == MAX_CHARS ){
  598 | 		chclif_char_delete2_ack( fd, char_id, 3, 0 );
  599 | 
  600 | 		return true;
  601 | 	}
  602 | 
  603 | 	if( SQL_SUCCESS != Sql_Query( sql_handle, "SELECT `delete_date`,`party_id`,`guild_id` FROM `%s` WHERE `char_id`='%d'", schema_config.char_db, char_id ) ){
  604 | 		Sql_ShowDebug( sql_handle );
  605 | 		chclif_char_delete2_ack( fd, char_id, 3, 0 );
  606 | 
  607 | 		return true;
  608 | 	}
  609 | 
  610 | 	// character not found
  611 | 	if( SQL_SUCCESS != Sql_NextRow( sql_handle ) ){
  612 | 		Sql_FreeResult( sql_handle );
  613 | 		chclif_char_delete2_ack( fd, char_id, 3, 0 );
  614 | 
  615 | 		return true;
  616 | 	}
  617 | 
  618 | 	char* data;
  619 | 
  620 | 	Sql_GetData( sql_handle, 0, &data, nullptr);
  621 | 	time_t delete_date = strtoul( data, nullptr, 10 );
  622 | 
  623 | 	Sql_GetData( sql_handle, 1, &data, nullptr );
  624 | 	uint32 party_id = strtoul( data, nullptr, 10 );
  625 | 
  626 | 	Sql_GetData( sql_handle, 2, &data, nullptr );
  627 | 	uint32 guild_id = strtoul( data, nullptr, 10 );
  628 | 
  629 | 	Sql_FreeResult( sql_handle );
  630 | 
  631 | 	// character already queued for deletion
  632 | 	if( delete_date ){
  633 | 		chclif_char_delete2_ack( fd, char_id, 0, 0 );
  634 | 
  635 | 		return true;
  636 | 	}
  637 | 
  638 | 	// character is in guild
  639 | 	if( charserv_config.char_config.char_del_restriction&CHAR_DEL_RESTRICT_GUILD && guild_id != 0 ){
  640 | 		chclif_char_delete2_ack( fd, char_id, 4, 0 );
  641 | 		return 1;
  642 | 	}
  643 | 
  644 | 	// character is in party
  645 | 	if( charserv_config.char_config.char_del_restriction&CHAR_DEL_RESTRICT_PARTY && party_id != 0 ){
  646 | 		chclif_char_delete2_ack( fd, char_id, 5, 0 );
  647 | 		return 1;
  648 | 	}
  649 | 
  650 | 	// success
  651 | 	delete_date = time( nullptr ) + charserv_config.char_config.char_del_delay;
  652 | 
  653 | 	if( SQL_SUCCESS != Sql_Query( sql_handle, "UPDATE `%s` SET `delete_date`='%lu' WHERE `char_id`='%d'", schema_config.char_db, (unsigned long)delete_date, char_id ) ){
  654 | 		Sql_ShowDebug( sql_handle );
  655 | 		chclif_char_delete2_ack( fd, char_id, 3, 0 );
  656 | 
  657 | 		return true;
  658 | 	}
  659 | 
  660 | 	chclif_char_delete2_ack( fd, char_id, 1, delete_date );
  661 | 
  662 | 	return true;
  663 | }
```
### Athena.NET HandleDeleteChar3AcceptAsync
```csharp
  778 |     private async Task HandleDeleteChar3AcceptAsync(byte[] packet, CancellationToken cancellationToken)
  779 |     {
  780 |         if (!_authenticated)
  781 |         {
  782 |             await SendDeleteChar3ResultAsync(0, 3, cancellationToken);
  783 |             return;
  784 |         }
  785 | 
  786 |         if (packet.Length < 12)
  787 |         {
  788 |             await SendDeleteChar3ResultAsync(0, 3, cancellationToken);
  789 |             return;
  790 |         }
  791 | 
  792 |         var charId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
  793 |         var birthdate = ConvertBirthdate(packet.AsSpan(6, 6));
  794 |         if (!IsDeleteBirthdateValid(birthdate))
  795 |         {
  796 |             await SendDeleteChar3ResultAsync(charId, 5, cancellationToken);
  797 |             return;
  798 |         }
  799 | 
  800 |         var config = _configStore.Current;
  801 |         var db = _dbFactory();
  802 |         if (db == null)
  803 |         {
  804 |             await SendDeleteChar3ResultAsync(charId, 3, cancellationToken);
  805 |             return;
  806 |         }
  807 | 
  808 |         var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
  809 |         await using (db)
  810 |         {
  811 |             var character = await db.Characters
  812 |                 .FirstOrDefaultAsync(c => c.CharId == charId && c.AccountId == _accountId, cancellationToken);
  813 |             if (character == null)
  814 |             {
  815 |                 await SendDeleteChar3ResultAsync(charId, 3, cancellationToken);
  816 |                 return;
  817 |             }
  818 | 
  819 |             if (IsDeleteRestricted(config, character))
  820 |             {
  821 |                 await SendDeleteChar3ResultAsync(charId, 2, cancellationToken);
  822 |                 return;
  823 |             }
  824 | 
  825 |             if (config.CharDeleteDelaySeconds > 0 && (character.DeleteDate == 0 || character.DeleteDate > now))
  826 |             {
  827 |                 await SendDeleteChar3ResultAsync(charId, 4, cancellationToken);
  828 |                 return;
  829 |             }
  830 | 
  831 |             db.Characters.Remove(character);
  832 |             await db.SaveChangesAsync(cancellationToken);
  833 |         }
  834 | 
  835 |         await SendDeleteChar3ResultAsync(charId, 1, cancellationToken);
  836 |     }
```
### rAthena chclif_parse_char_delete2_accept
```cpp
  697 | bool chclif_parse_char_delete2_accept( int32 fd, char_session_data& sd ){
  698 | 	const PACKET_CH_DELETE_CHAR3* p = reinterpret_cast<PACKET_CH_DELETE_CHAR3*>( RFIFOP( fd, 0 ) );
  699 | 
  700 | 	uint32 char_id = p->CID;
  701 | 
  702 | 	ShowInfo( CL_RED "Request Char Deletion: " CL_GREEN "%d (%d)" CL_RESET "\n", sd.account_id, char_id );
  703 | 
  704 | 	// construct "YY-MM-DD"
  705 | 	char birthdate[8 + 1];
  706 | 
  707 | 	birthdate[0] = p->birthdate[0];
  708 | 	birthdate[1] = p->birthdate[1];
  709 | 	birthdate[2] = '-';
  710 | 	birthdate[3] = p->birthdate[2];
  711 | 	birthdate[4] = p->birthdate[3];
  712 | 	birthdate[5] = '-';
  713 | 	birthdate[6] = p->birthdate[4];
  714 | 	birthdate[7] = p->birthdate[5];
  715 | 	birthdate[8] = '\0';
  716 | 
  717 | 	// Only check for birthdate
  718 | 	if( !chclif_delchar_check( &sd, birthdate, CHAR_DEL_BIRTHDATE ) ){
  719 | 		chclif_char_delete2_accept_ack( fd, char_id, 5 );
  720 | 
  721 | 		return true;
  722 | 	}
  723 | 
  724 | 	switch( char_delete( &sd, char_id ) ){
  725 | 		// success
  726 | 		case CHAR_DELETE_OK:
  727 | 			chclif_char_delete2_accept_ack( fd, char_id, 1 );
  728 | 			break;
  729 | 		// data error
  730 | 		case CHAR_DELETE_DATABASE:
  731 | 		// character not found
  732 | 		case CHAR_DELETE_NOTFOUND:
  733 | 			chclif_char_delete2_accept_ack( fd, char_id, 3 );
  734 | 			break;
  735 | 		// in a party
  736 | 		case CHAR_DELETE_PARTY:
  737 | 		// in a guild
  738 | 		case CHAR_DELETE_GUILD:
  739 | 		// character level config restriction
  740 | 		case CHAR_DELETE_BASELEVEL:
  741 | 			chclif_char_delete2_accept_ack( fd, char_id, 2 );
  742 | 			break;
  743 | 		// not queued or delay not yet passed
  744 | 		case CHAR_DELETE_TIME:
  745 | 			chclif_char_delete2_accept_ack( fd, char_id, 4 );
  746 | 			break;
  747 | 	}
  748 | 
  749 | 	return true;
  750 | }
```
### Athena.NET HandleDeleteChar3CancelAsync
```csharp
  838 |     private async Task HandleDeleteChar3CancelAsync(byte[] packet, CancellationToken cancellationToken)
  839 |     {
  840 |         if (!_authenticated)
  841 |         {
  842 |             await SendDeleteChar3CancelAsync(0, 2, cancellationToken);
  843 |             return;
  844 |         }
  845 | 
  846 |         var charId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
  847 |         var db = _dbFactory();
  848 |         if (db == null)
  849 |         {
  850 |             await SendDeleteChar3CancelAsync(charId, 2, cancellationToken);
  851 |             return;
  852 |         }
  853 | 
  854 |         await using (db)
  855 |         {
  856 |             var character = await db.Characters
  857 |                 .FirstOrDefaultAsync(c => c.CharId == charId && c.AccountId == _accountId, cancellationToken);
  858 |             if (character == null || character.DeleteDate == 0)
  859 |             {
  860 |                 await SendDeleteChar3CancelAsync(charId, 2, cancellationToken);
  861 |                 return;
  862 |             }
  863 | 
  864 |             character.DeleteDate = 0;
  865 |             await db.SaveChangesAsync(cancellationToken);
  866 |         }
  867 | 
  868 |         await SendDeleteChar3CancelAsync(charId, 1, cancellationToken);
  869 |     }
```
### rAthena chclif_parse_char_delete2_cancel
```cpp
  753 | bool chclif_parse_char_delete2_cancel( int32 fd, char_session_data& sd ){
  754 | 	const PACKET_CH_DELETE_CHAR3_CANCEL* p = reinterpret_cast<PACKET_CH_DELETE_CHAR3_CANCEL*>( RFIFOP( fd, 0 ) );
  755 | 
  756 | 	size_t i;
  757 | 
  758 | 	ARR_FIND( 0, MAX_CHARS, i, sd.found_char[i] == p->CID );
  759 | 
  760 | 	// character not found
  761 | 	if( i == MAX_CHARS ){
  762 | 		chclif_char_delete2_cancel_ack( fd, p->CID, 2 );
  763 | 
  764 | 		return true;
  765 | 	}
  766 | 
  767 | 	// there is no need to check, whether or not the character was
  768 | 	// queued for deletion, as the client prints an error message by
  769 | 	// itself, if it was not the case (@see char_delete2_cancel_ack)
  770 | 	if( SQL_SUCCESS != Sql_Query( sql_handle, "UPDATE `%s` SET `delete_date`='0' WHERE `char_id`='%d'", schema_config.char_db, p->CID ) ){
  771 | 		Sql_ShowDebug(sql_handle);
  772 | 		chclif_char_delete2_cancel_ack( fd, p->CID, 2 );
  773 | 
  774 | 		return true;
  775 | 	}
  776 | 
  777 | 	chclif_char_delete2_cancel_ack( fd, p->CID, 1 );
  778 | 
  779 | 	return true;
  780 | }
```
### Athena.NET HandlePincodeWindowAsync
```csharp
  871 |     private async Task HandlePincodeWindowAsync(byte[] packet, CancellationToken cancellationToken)
  872 |     {
  873 |         if (!_authenticated)
  874 |         {
  875 |             return;
  876 |         }
  877 | 
  878 |         var accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
  879 |         if (accountId != _accountId)
  880 |         {
  881 |             return;
  882 |         }
  883 | 
  884 |         await SendPincodeStartAsync(cancellationToken);
  885 |     }
```
### rAthena chclif_parse_reqpincode_window
```cpp
  174 | bool chclif_parse_reqpincode_window( int32 fd, char_session_data& sd ){
  175 | 	const PACKET_CH_AVAILABLE_SECOND_PASSWD* p = reinterpret_cast<PACKET_CH_AVAILABLE_SECOND_PASSWD*>( RFIFOP( fd, 0 ) );
  176 | 
  177 | 	if( p->AID != sd.account_id ){
  178 | 		return false;
  179 | 	}
  180 | 
  181 | 	if( charserv_config.pincode_config.pincode_enabled == 0 ){
  182 | 		return true;
  183 | 	}
  184 | 
  185 | 	if( strlen( sd.pincode ) <= 0 ){
  186 | 		chclif_pincode_sendstate( fd, sd, PINCODE_NEW );
  187 | 	}else{
  188 | 		chclif_pincode_sendstate( fd, sd, PINCODE_ASK );
  189 | 	}
  190 | 
  191 | 	return true;
  192 | }
```
### Athena.NET HandlePincodeCheckAsync
```csharp
  887 |     private async Task HandlePincodeCheckAsync(byte[] packet, CancellationToken cancellationToken)
  888 |     {
  889 |         if (!_authenticated)
  890 |         {
  891 |             return;
  892 |         }
  893 | 
  894 |         if (packet.Length < 10)
  895 |         {
  896 |             _client.Close();
  897 |             return;
  898 |         }
  899 | 
  900 |         var config = _configStore.Current;
  901 |         if (!config.PincodeEnabled)
  902 |         {
  903 |             _client.Close();
  904 |             return;
  905 |         }
  906 | 
  907 |         var accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
  908 |         if (accountId != _accountId)
  909 |         {
  910 |             _client.Close();
  911 |             return;
  912 |         }
  913 | 
  914 |         var pin = ReadFixedString(packet.AsSpan(6, 4));
  915 |         var decrypted = DecryptPincode(_pincodeSeed, pin);
  916 |         if (decrypted == null)
  917 |         {
  918 |             _client.Close();
  919 |             return;
  920 |         }
  921 | 
  922 |         if (string.Equals(_pincode, decrypted, StringComparison.Ordinal))
  923 |         {
  924 |             _pincodeTry = 0;
  925 |             _pincodeCorrect = true;
  926 |             PincodePassed[_accountId] = true;
  927 |             await SendPincodeStateAsync(PincodeState.Passed, cancellationToken);
  928 |             return;
  929 |         }
  930 | 
  931 |         _pincodeTry += 1;
  932 |         await SendPincodeStateAsync(PincodeState.Wrong, cancellationToken);
  933 | 
  934 |         if (config.PincodeMaxTry > 0 && _pincodeTry >= config.PincodeMaxTry)
  935 |         {
  936 |             _loginConnector.TrySendPincodeAuthFail(_accountId);
  937 |         }
  938 |     }
```
### rAthena chclif_parse_pincode_check
```cpp
  197 | bool chclif_parse_pincode_check( int32 fd, char_session_data& sd ){
  198 | 	const PACKET_CH_SECOND_PASSWD_ACK* p = reinterpret_cast<PACKET_CH_SECOND_PASSWD_ACK*>( RFIFOP( fd, 0 ) );
  199 | 
  200 | 	if( charserv_config.pincode_config.pincode_enabled == 0 ){
  201 | 		set_eof(fd);
  202 | 		return false;
  203 | 	}
  204 | 
  205 | 	if( p->AID != sd.account_id ){
  206 | 		set_eof(fd);
  207 | 		return false;
  208 | 	}
  209 | 
  210 | 	char pin[PINCODE_LENGTH + 1];
  211 | 
  212 | 	safestrncpy( pin, p->pin, PINCODE_LENGTH + 1 );
  213 | 
  214 | 	if (!char_pincode_decrypt(sd.pincode_seed, pin )) {
  215 | 		set_eof(fd);
  216 | 		return false;
  217 | 	}
  218 | 
  219 | 	if( char_pincode_compare( fd, sd, pin ) ){
  220 | 		sd.pincode_correct = true;
  221 | 		chclif_pincode_sendstate( fd, sd, PINCODE_PASSED );
  222 | 	}
  223 | 
  224 | 	return true;
  225 | }
```
### Athena.NET HandlePincodeChangeAsync
```csharp
  940 |     private async Task HandlePincodeChangeAsync(byte[] packet, CancellationToken cancellationToken)
  941 |     {
  942 |         if (!_authenticated)
  943 |         {
  944 |             return;
  945 |         }
  946 | 
  947 |         if (packet.Length < 14)
  948 |         {
  949 |             _client.Close();
  950 |             return;
  951 |         }
  952 | 
  953 |         var config = _configStore.Current;
  954 |         if (!config.PincodeEnabled)
  955 |         {
  956 |             _client.Close();
  957 |             return;
  958 |         }
  959 | 
  960 |         var accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
  961 |         if (accountId != _accountId)
  962 |         {
  963 |             _client.Close();
  964 |             return;
  965 |         }
  966 | 
  967 |         var oldPin = ReadFixedString(packet.AsSpan(6, 4));
  968 |         var newPin = ReadFixedString(packet.AsSpan(10, 4));
  969 |         var decryptedOld = DecryptPincode(_pincodeSeed, oldPin);
  970 |         var decryptedNew = DecryptPincode(_pincodeSeed, newPin);
  971 |         if (decryptedOld == null || decryptedNew == null)
  972 |         {
  973 |             _client.Close();
  974 |             return;
  975 |         }
  976 | 
  977 |         if (!string.Equals(_pincode, decryptedOld, StringComparison.Ordinal))
  978 |         {
  979 |             _pincodeTry += 1;
  980 |             await SendPincodeStateAsync(PincodeState.Wrong, cancellationToken);
  981 | 
  982 |             if (config.PincodeMaxTry > 0 && _pincodeTry >= config.PincodeMaxTry)
  983 |             {
  984 |                 _loginConnector.TrySendPincodeAuthFail(_accountId);
  985 |             }
  986 |             return;
  987 |         }
  988 | 
  989 |         if (!IsPincodeAllowed(config, decryptedNew))
  990 |         {
  991 |             await SendPincodeStateAsync(PincodeState.Illegal, cancellationToken);
  992 |             return;
  993 |         }
  994 | 
  995 |         _loginConnector.TrySendPincodeUpdate(_accountId, decryptedNew);
  996 |         _pincode = decryptedNew;
  997 |         _pincodeCorrect = true;
  998 |         PincodePassed[_accountId] = true;
  999 |         _pincodeChange = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
 1000 |         _pincodeTry = 0;
 1001 |         await SendPincodeStateAsync(PincodeState.Passed, cancellationToken);
 1002 |     }
```
### rAthena chclif_parse_pincode_change
```cpp
  300 | bool chclif_parse_pincode_change( int32 fd, char_session_data& sd ){
  301 | 	const PACKET_CH_EDIT_SECOND_PASSWD* p = reinterpret_cast<PACKET_CH_EDIT_SECOND_PASSWD*>( RFIFOP( fd, 0 ) );
  302 | 
  303 | 	if( p->AID != sd.account_id ){
  304 | 		set_eof(fd);
  305 | 		return false;
  306 | 	}
  307 | 
  308 | 	if( charserv_config.pincode_config.pincode_enabled == 0 ){
  309 | 		set_eof(fd);
  310 | 		return false;
  311 | 	}
  312 | 
  313 | 	char oldpin[PINCODE_LENGTH + 1];
  314 | 	char newpin[PINCODE_LENGTH + 1];
  315 | 
  316 | 	safestrncpy( oldpin, p->old_pin, PINCODE_LENGTH + 1 );
  317 | 	safestrncpy( newpin, p->new_pin, PINCODE_LENGTH + 1 );
  318 | 
  319 | 	if (!char_pincode_decrypt(sd.pincode_seed,oldpin) || !char_pincode_decrypt(sd.pincode_seed,newpin)) {
  320 | 		set_eof(fd);
  321 | 		return 1;
  322 | 	}
  323 | 
  324 | 	if( !char_pincode_compare( fd, sd, oldpin ) ){
  325 | 		return true;
  326 | 	}
  327 | 
  328 | 	if( !pincode_allowed( newpin ) ){
  329 | 		chclif_pincode_sendstate( fd, sd, PINCODE_ILLEGAL );
  330 | 
  331 | 		return true;
  332 | 	}
  333 | 
  334 | 	chlogif_pincode_notifyLoginPinUpdate( sd.account_id, newpin );
  335 | 	sd.pincode_correct = true;
  336 | 
  337 | 	safestrncpy( sd.pincode, newpin, sizeof( sd.pincode ) );
  338 | 
  339 | 	ShowInfo( "Pincode changed for AID: %u\n", sd.account_id );
  340 | 		
  341 | 	chclif_pincode_sendstate( fd, sd, PINCODE_PASSED );
  342 | 
  343 | 	return true;
  344 | }
```
### Athena.NET HandlePincodeSetAsync
```csharp
 1004 |     private async Task HandlePincodeSetAsync(byte[] packet, CancellationToken cancellationToken)
 1005 |     {
 1006 |         if (!_authenticated)
 1007 |         {
 1008 |             return;
 1009 |         }
 1010 | 
 1011 |         var config = _configStore.Current;
 1012 |         if (!config.PincodeEnabled)
 1013 |         {
 1014 |             _client.Close();
 1015 |             return;
 1016 |         }
 1017 | 
 1018 |         var accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
 1019 |         if (accountId != _accountId)
 1020 |         {
 1021 |             _client.Close();
 1022 |             return;
 1023 |         }
 1024 | 
 1025 |         var newPin = ReadFixedString(packet.AsSpan(6, 4));
 1026 |         var decryptedNew = DecryptPincode(_pincodeSeed, newPin);
 1027 |         if (decryptedNew == null)
 1028 |         {
 1029 |             _client.Close();
 1030 |             return;
 1031 |         }
 1032 | 
 1033 |         if (!IsPincodeAllowed(config, decryptedNew))
 1034 |         {
 1035 |             await SendPincodeStateAsync(PincodeState.Illegal, cancellationToken);
 1036 |             return;
 1037 |         }
 1038 | 
 1039 |         _loginConnector.TrySendPincodeUpdate(_accountId, decryptedNew);
 1040 |         _pincode = decryptedNew;
 1041 |         _pincodeCorrect = true;
 1042 |         PincodePassed[_accountId] = true;
 1043 |         _pincodeChange = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
 1044 |         _pincodeTry = 0;
 1045 |         await SendPincodeStateAsync(PincodeState.Passed, cancellationToken);
 1046 |     }
```
### rAthena chclif_parse_pincode_setnew
```cpp
  349 | bool chclif_parse_pincode_setnew( int32 fd, char_session_data& sd ){
  350 | 	const PACKET_CH_MAKE_SECOND_PASSWD* p = reinterpret_cast<PACKET_CH_MAKE_SECOND_PASSWD*>( RFIFOP( fd, 0 ) );
  351 | 
  352 | 	if( p->AID != sd.account_id ){
  353 | 		set_eof(fd);
  354 | 		return false;
  355 | 	}
  356 | 
  357 | 	if( charserv_config.pincode_config.pincode_enabled == 0 ){
  358 | 		set_eof(fd);
  359 | 		return false;
  360 | 	}
  361 | 
  362 | 	char newpin[PINCODE_LENGTH + 1];
  363 | 
  364 | 	safestrncpy( newpin, p->pin, PINCODE_LENGTH + 1 );
  365 | 
  366 | 	if( !char_pincode_decrypt( sd.pincode_seed, newpin ) ){
  367 | 		set_eof(fd);
  368 | 		return 1;
  369 | 	}
  370 | 
  371 | 	if( !pincode_allowed( newpin ) ){
  372 | 		chclif_pincode_sendstate( fd, sd, PINCODE_ILLEGAL );
  373 | 
  374 | 		return true;
  375 | 	}
  376 | 
  377 | 	chlogif_pincode_notifyLoginPinUpdate( sd.account_id, newpin );
  378 | 
  379 | 	safestrncpy( sd.pincode, newpin, sizeof( sd.pincode ) );
  380 | 
  381 | 	ShowInfo( "Pincode added for AID: %u\n", sd.account_id );
  382 | 
  383 | 	sd.pincode_correct = true;
  384 | 	chclif_pincode_sendstate( fd, sd, PINCODE_PASSED );
  385 | 
  386 | 	return true;
  387 | }
```
### Athena.NET HandleRenameCheckAsync
```csharp
 1048 |     private async Task HandleRenameCheckAsync(byte[] packet, CancellationToken cancellationToken)
 1049 |     {
 1050 |         if (!_authenticated)
 1051 |         {
 1052 |             return;
 1053 |         }
 1054 | 
 1055 |         if (packet.Length < 34)
 1056 |         {
 1057 |             await SendRenameCheckAsync(false, cancellationToken);
 1058 |             return;
 1059 |         }
 1060 | 
 1061 |         var accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
 1062 |         if (accountId != _accountId)
 1063 |         {
 1064 |             return;
 1065 |         }
 1066 | 
 1067 |         var charId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(6, 4));
 1068 |         var name = ReadFixedString(packet.AsSpan(10, 24));
 1069 |         var normalized = NormalizeName(name);
 1070 |         var db = _dbFactory();
 1071 |         if (db == null)
 1072 |         {
 1073 |             await SendRenameCheckAsync(false, cancellationToken);
 1074 |             return;
 1075 |         }
 1076 | 
 1077 |         await using (db)
 1078 |         {
 1079 |             var exists = await db.Characters
 1080 |                 .AnyAsync(c => c.CharId == charId && c.AccountId == _accountId, cancellationToken);
 1081 |             if (!exists)
 1082 |             {
 1083 |                 await SendRenameCheckAsync(false, cancellationToken);
 1084 |                 return;
 1085 |             }
 1086 |         }
 1087 | 
 1088 |         var validation = await ValidateCharNameAsync(normalized, cancellationToken);
 1089 |         if (validation == NameValidationResult.Ok)
 1090 |         {
 1091 |             _pendingRenameName = normalized;
 1092 |             await SendRenameCheckAsync(true, cancellationToken);
 1093 |             return;
 1094 |         }
 1095 | 
 1096 |         await SendRenameCheckAsync(false, cancellationToken);
 1097 |     }
```
### rAthena chclif_parse_reqrename
```cpp
 1421 | bool chclif_parse_reqrename( int32 fd, char_session_data& sd ){
 1422 | 	const PACKET_CH_REQ_IS_VALID_CHARNAME* p = reinterpret_cast<PACKET_CH_REQ_IS_VALID_CHARNAME*>( RFIFOP( fd, 0 ) );
 1423 | 
 1424 | 	if( p->AID != sd.account_id ){
 1425 | 		return false;
 1426 | 	}
 1427 | 
 1428 | 	size_t i;
 1429 | 
 1430 | 	ARR_FIND( 0, MAX_CHARS, i, sd.found_char[i] == p->CID );
 1431 | 
 1432 | 	if( i == MAX_CHARS ){
 1433 | 		return true;
 1434 | 	}
 1435 | 
 1436 | 	char name[NAME_LENGTH];
 1437 | 
 1438 | 	safestrncpy( name, p->new_name, NAME_LENGTH );
 1439 | 
 1440 | 	normalize_name( name, TRIM_CHARS );
 1441 | 
 1442 | 	char esc_name[NAME_LENGTH * 2 + 1];
 1443 | 
 1444 | 	Sql_EscapeStringLen( sql_handle, esc_name, name, strnlen( name, NAME_LENGTH ) );
 1445 | 
 1446 | 	if( char_check_char_name( name, esc_name ) != 0 ){
 1447 | 		chclif_reqrename_response( fd, false );
 1448 | 
 1449 | 		return true;	
 1450 | 	}
 1451 | 
 1452 | 	// Name is okay
 1453 | 	safestrncpy( sd.new_name, name, NAME_LENGTH );
 1454 | 
 1455 | 	chclif_reqrename_response( fd, true );
 1456 | 
 1457 | 	return 1;
 1458 | }
```
### Athena.NET HandleRenameApplyAsync
```csharp
 1099 |     private async Task HandleRenameApplyAsync(byte[] packet, CancellationToken cancellationToken)
 1100 |     {
 1101 |         if (!_authenticated)
 1102 |         {
 1103 |             return;
 1104 |         }
 1105 | 
 1106 |         if (packet.Length < 30)
 1107 |         {
 1108 |             await SendRenameResultAsync(2, cancellationToken);
 1109 |             return;
 1110 |         }
 1111 | 
 1112 |         var charId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
 1113 |         var newName = ReadFixedString(packet.AsSpan(6, 24));
 1114 |         var normalized = NormalizeName(newName);
 1115 |         if (string.IsNullOrWhiteSpace(normalized))
 1116 |         {
 1117 |             normalized = _pendingRenameName;
 1118 |         }
 1119 | 
 1120 |         _pendingRenameName = string.Empty;
 1121 |         if (string.IsNullOrWhiteSpace(normalized))
 1122 |         {
 1123 |             await SendRenameResultAsync(2, cancellationToken);
 1124 |             return;
 1125 |         }
 1126 | 
 1127 |         var db = _dbFactory();
 1128 |         if (db == null)
 1129 |         {
 1130 |             await SendRenameResultAsync(3, cancellationToken);
 1131 |             return;
 1132 |         }
 1133 | 
 1134 |         var config = _configStore.Current;
 1135 |         await using (db)
 1136 |         {
 1137 |             var character = await db.Characters
 1138 |                 .FirstOrDefaultAsync(c => c.CharId == charId && c.AccountId == _accountId, cancellationToken);
 1139 |             if (character == null)
 1140 |             {
 1141 |                 await SendRenameResultAsync(2, cancellationToken);
 1142 |                 return;
 1143 |             }
 1144 | 
 1145 |             if (!string.IsNullOrEmpty(normalized) && string.Equals(character.Name, normalized, StringComparison.Ordinal))
 1146 |             {
 1147 |                 await SendRenameResultAsync(0, cancellationToken);
 1148 |                 return;
 1149 |             }
 1150 | 
 1151 |             if (character.Rename == 0)
 1152 |             {
 1153 |                 await SendRenameResultAsync(1, cancellationToken);
 1154 |                 return;
 1155 |             }
 1156 | 
 1157 |             if (!config.CharRenameParty && character.PartyId != 0)
 1158 |             {
 1159 |                 await SendRenameResultAsync(6, cancellationToken);
 1160 |                 return;
 1161 |             }
 1162 | 
 1163 |             if (!config.CharRenameGuild && character.GuildId != 0)
 1164 |             {
 1165 |                 await SendRenameResultAsync(5, cancellationToken);
 1166 |                 return;
 1167 |             }
 1168 | 
 1169 |             var validation = await ValidateCharNameAsync(normalized, cancellationToken);
 1170 |             if (validation == NameValidationResult.Exists)
 1171 |             {
 1172 |                 await SendRenameResultAsync(4, cancellationToken);
 1173 |                 return;
 1174 |             }
 1175 | 
 1176 |             if (validation != NameValidationResult.Ok)
 1177 |             {
 1178 |                 await SendRenameResultAsync(8, cancellationToken);
 1179 |                 return;
 1180 |             }
 1181 | 
 1182 |             character.Name = normalized;
 1183 |             character.Rename = (ushort)Math.Max(0, character.Rename - 1);
 1184 |             await db.SaveChangesAsync(cancellationToken);
 1185 |         }
 1186 | 
 1187 |         await SendRenameResultAsync(0, cancellationToken);
 1188 |         await SendCharListAsync(cancellationToken);
 1189 |     }
```
### rAthena chclif_parse_ackrename
```cpp
 1547 | bool chclif_parse_ackrename( int32 fd, char_session_data& sd ){
 1548 | 	const PACKET_CH_REQ_CHANGE_CHARNAME* p = reinterpret_cast<PACKET_CH_REQ_CHANGE_CHARNAME*>( RFIFOP( fd, 0 ) );
 1549 | 
 1550 | 	size_t i;
 1551 | 	uint32 cid = p->CID;
 1552 | 
 1553 | 	ARR_FIND( 0, MAX_CHARS, i, sd.found_char[i] == cid );
 1554 | 
 1555 | 	if( i == MAX_CHARS ){
 1556 | 		return true;
 1557 | 	}
 1558 | 
 1559 | #if PACKETVER >= 20111101
 1560 | 	char name[NAME_LENGTH], esc_name[NAME_LENGTH * 2 + 1];
 1561 | 
 1562 | 	safestrncpy( name, p->new_name, NAME_LENGTH );
 1563 | 
 1564 | 	normalize_name( name, TRIM_CHARS );
 1565 | 	Sql_EscapeStringLen( sql_handle, esc_name, name, strnlen( name, NAME_LENGTH ) );
 1566 | 
 1567 | 	safestrncpy( sd.new_name, name, NAME_LENGTH );
 1568 | #endif
 1569 | 
 1570 | 	// Start the renaming process
 1571 | 	int16 result = char_rename_char_sql( &sd, cid );
 1572 | 
 1573 | 	chclif_rename_response( fd, result );
 1574 | 
 1575 | #if PACKETVER >= 20111101
 1576 | 	// If the renaming was successful, we need to resend the characters
 1577 | 	if( result == 0 ){
 1578 | 		chclif_mmo_char_send( fd, sd );
 1579 | 	}
 1580 | #endif
 1581 | 
 1582 | 	return true;
 1583 | }
```
### Athena.NET HandleMoveCharSlotAsync
```csharp
 1191 |     private async Task HandleMoveCharSlotAsync(byte[] packet, CancellationToken cancellationToken)
 1192 |     {
 1193 |         if (!_authenticated)
 1194 |         {
 1195 |             return;
 1196 |         }
 1197 | 
 1198 |         var fromSlot = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2, 2));
 1199 |         var toSlot = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(4, 2));
 1200 |         var config = _configStore.Current;
 1201 | 
 1202 |         if (fromSlot >= config.MaxChars)
 1203 |         {
 1204 |             await SendMoveCharSlotAckAsync(1, 0, cancellationToken);
 1205 |             return;
 1206 |         }
 1207 | 
 1208 |         if (!config.CharMoveEnabled)
 1209 |         {
 1210 |             await SendMoveCharSlotAckAsync(1, 0, cancellationToken);
 1211 |             return;
 1212 |         }
 1213 | 
 1214 |         if (toSlot >= _charSlots)
 1215 |         {
 1216 |             await SendMoveCharSlotAckAsync(1, 0, cancellationToken);
 1217 |             return;
 1218 |         }
 1219 | 
 1220 |         var db = _dbFactory();
 1221 |         if (db == null)
 1222 |         {
 1223 |             await SendMoveCharSlotAckAsync(1, 0, cancellationToken);
 1224 |             return;
 1225 |         }
 1226 | 
 1227 |         await using (db)
 1228 |         {
 1229 |             var characters = await db.Characters
 1230 |                 .Where(c => c.AccountId == _accountId)
 1231 |                 .ToListAsync(cancellationToken);
 1232 | 
 1233 |             var fromChar = characters.FirstOrDefault(c => c.CharNum == fromSlot);
 1234 |             if (fromChar == null)
 1235 |             {
 1236 |                 await SendMoveCharSlotAckAsync(1, 0, cancellationToken);
 1237 |                 return;
 1238 |             }
 1239 | 
 1240 |             var remainingMoves = (ushort)Math.Min(fromChar.Moves, ushort.MaxValue);
 1241 |             if (!config.CharMovesUnlimited && remainingMoves == 0)
 1242 |             {
 1243 |                 await SendMoveCharSlotAckAsync(1, remainingMoves, cancellationToken);
 1244 |                 return;
 1245 |             }
 1246 | 
 1247 |             var toChar = characters.FirstOrDefault(c => c.CharNum == toSlot);
 1248 |             if (toChar != null)
 1249 |             {
 1250 |                 if (!config.CharMoveToUsed)
 1251 |                 {
 1252 |                     await SendMoveCharSlotAckAsync(1, remainingMoves, cancellationToken);
 1253 |                     return;
 1254 |                 }
 1255 | 
 1256 |                 var temp = fromChar.CharNum;
 1257 |                 fromChar.CharNum = toChar.CharNum;
 1258 |                 toChar.CharNum = temp;
 1259 |             }
 1260 |             else
 1261 |             {
 1262 |                 fromChar.CharNum = (byte)Math.Clamp((int)toSlot, 0, byte.MaxValue);
 1263 |             }
 1264 | 
 1265 |             if (!config.CharMovesUnlimited && fromChar.Moves > 0)
 1266 |             {
 1267 |                 fromChar.Moves -= 1;
 1268 |             }
 1269 | 
 1270 |             await db.SaveChangesAsync(cancellationToken);
 1271 | 
 1272 |             remainingMoves = (ushort)Math.Min(fromChar.Moves, ushort.MaxValue);
 1273 |             await SendMoveCharSlotAckAsync(0, remainingMoves, cancellationToken);
 1274 |         }
 1275 | 
 1276 |         await SendCharListAsync(cancellationToken);
 1277 |     }
```
### rAthena chclif_parse_moveCharSlot
```cpp
   72 | bool chclif_parse_moveCharSlot( int32 fd, char_session_data& sd ){
   73 | 	const PACKET_CH_REQ_CHANGE_CHARACTER_SLOT* p = reinterpret_cast<PACKET_CH_REQ_CHANGE_CHARACTER_SLOT*>( RFIFOP( fd, 0 ) );
   74 | 
   75 | 	uint16 from = p->slot_before;
   76 | 	uint16 to = p->slot_after;
   77 | 
   78 | 	// Bounds check
   79 | 	if( from >= MAX_CHARS ){
   80 | 		chclif_moveCharSlotReply( fd, sd, from, 1 );
   81 | 		return 1;
   82 | 	}
   83 | 
   84 | 	// Have we changed too often or is it disabled?
   85 | 	if( (charserv_config.charmove_config.char_move_enabled)==0
   86 | 	|| ( (charserv_config.charmove_config.char_moves_unlimited)==0 && sd.char_moves[from] <= 0 ) ){
   87 | 		chclif_moveCharSlotReply( fd, sd, from, 1 );
   88 | 		return true;
   89 | 	}
   90 | 
   91 | 	// Check if there is a character on this slot
   92 | 	if( sd.found_char[from] <= 0 ){
   93 | 		chclif_moveCharSlotReply( fd, sd, from, 1 );
   94 | 		return true;
   95 | 	}
   96 | 
   97 | 	// Bounds check
   98 | 	if( to >= MAX_CHARS ){
   99 | 		chclif_moveCharSlotReply( fd, sd, from, 1 );
  100 | 		return 1;
  101 | 	}
  102 | 
  103 | 	// Check maximum allowed char slot for this account
  104 | 	if( to >= sd.char_slots ){
  105 | 		chclif_moveCharSlotReply( fd, sd, from, 1 );
  106 | 		return 1;
  107 | 	}
  108 | 
  109 | 	if( sd.found_char[to] > 0 ){
  110 | 		// We want to move to a used position
  111 | 		if( charserv_config.charmove_config.char_movetoused ){ // TODO: check if the target is in deletion process
  112 | 			// Admin is friendly and uses triangle exchange
  113 | 			if( SQL_ERROR == Sql_QueryStr(sql_handle, "START TRANSACTION")
  114 | 				|| SQL_ERROR == Sql_Query(sql_handle, "UPDATE `%s` SET `char_num`='%d' WHERE `char_id` = '%d'",schema_config.char_db, to, sd.found_char[from] )
  115 | 				|| SQL_ERROR == Sql_Query(sql_handle, "UPDATE `%s` SET `char_num`='%d' WHERE `char_id` = '%d'", schema_config.char_db, from, sd.found_char[to] )
  116 | 				|| SQL_ERROR == Sql_QueryStr(sql_handle, "COMMIT")
  117 | 				){
  118 | 				chclif_moveCharSlotReply( fd, sd, from, 1 );
  119 | 				Sql_ShowDebug(sql_handle);
  120 | 				Sql_QueryStr(sql_handle,"ROLLBACK");
  121 | 				return true;
  122 | 			}
  123 | 		}else{
  124 | 			// Admin doesn't allow us to
  125 | 			chclif_moveCharSlotReply( fd, sd, from, 1 );
  126 | 			return true;
  127 | 		}
  128 | 	}else if( SQL_ERROR == Sql_Query(sql_handle, "UPDATE `%s` SET `char_num`='%d' WHERE `char_id`='%d'", schema_config.char_db, to, sd.found_char[from] ) ){
  129 | 		Sql_ShowDebug(sql_handle);
  130 | 		chclif_moveCharSlotReply( fd, sd, from, 1 );
  131 | 		return true;
  132 | 	}
  133 | 
  134 | 	if( (charserv_config.charmove_config.char_moves_unlimited)==0 ){
  135 | 		sd.char_moves[from]--;
  136 | 		Sql_Query(sql_handle, "UPDATE `%s` SET `moves`='%d' WHERE `char_id`='%d'", schema_config.char_db, sd.char_moves[from], sd.found_char[from] );
  137 | 	}
  138 | 
  139 | 	// We successfully moved the char - time to notify the client
  140 | 	chclif_moveCharSlotReply( fd, sd, from, 0 );
  141 | 	chclif_mmo_char_send(fd, sd);
  142 | 
  143 | 	return true;
  144 | }
```
