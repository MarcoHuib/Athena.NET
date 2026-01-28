## Parity table
| Step | Athena.NET behavior | rAthena behavior | Match | Notes |
|---|---|---|---|---|
| 1 | Ignore repeated enter requests when _authRequested is true | Reject if session already has sd (character already logged in) | PARTIAL | Both prevent duplicate enter, but checks differ. |
| 2 | Parse account_id/char_id/login_id1/sex from packet | Parse account_id/char_id/login_id1/sex via packet_db positions | YES | Both extract same core fields. |
| 3 | No explicit packet validation in HandleEnterAsync | clif_parse_WantToConnection_sub validates length/account/char/sex | DIFF | rAthena rejects invalid fields; Athena does not in this method. |
| 4 | Send auth request to char server; refuse if request fails | On error, disconnects with info log; auth handled in map auth node flow | PARTIAL | Both rely on downstream auth but failure handling differs. |

## Concrete parity gaps
- Packet validation: rAthena validates length/account/char/sex in clif_parse_WantToConnection_sub; Athena has no explicit validation in HandleEnterAsync.
- Failure handling: Athena refuses enter when TrySendAuthRequest fails; rAthena logs and disconnects on parse errors.

## Evidence
### Athena.NET HandleEnterAsync
```csharp
  110 |     private async Task HandleEnterAsync(byte[] packet, CancellationToken cancellationToken)
  111 |     {
  112 |         if (_authRequested)
  113 |         {
  114 |             return;
  115 |         }
  116 | 
  117 |         _accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
  118 |         _charId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(6, 4));
  119 |         _loginId1 = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(10, 4));
  120 |         _sex = packet[18];
  121 | 
  122 |         var endpoint = _client.Client.RemoteEndPoint as IPEndPoint;
  123 |         var clientIp = endpoint?.Address ?? IPAddress.Loopback;
  124 | 
  125 |         if (!_charConnector.TrySendAuthRequest(this, _accountId, _charId, _loginId1, _sex, clientIp))
  126 |         {
  127 |             await SendRefuseEnterAsync(0, cancellationToken);
  128 |             return;
  129 |         }
  130 | 
  131 |         _authRequested = true;
  132 |     }
```
### rAthena clif_parse_WantToConnection
```cpp
10639 | void clif_parse_WantToConnection(int32 fd, map_session_data* sd)
10640 | {
10641 | 	block_list* bl;
10642 | 	struct auth_node* node;
10643 | 	int32 cmd, account_id, char_id, login_id1, sex, err;
10644 | 	t_tick client_tick; //The client tick is a tick, therefore it needs be unsigned. [Skotlex]
10645 | 
10646 | 	if (sd) {
10647 | 		ShowError("clif_parse_WantToConnection : invalid request (character already logged in)\n");
10648 | 		return;
10649 | 	}
10650 | 
10651 | 	cmd = RFIFOW(fd, 0);
10652 | 	// TODO: shuffle packet
10653 | 	account_id = RFIFOL(fd, packet_db[cmd].pos[0]);
10654 | 	char_id = RFIFOL(fd, packet_db[cmd].pos[1]);
10655 | 	login_id1 = RFIFOL(fd, packet_db[cmd].pos[2]);
10656 | 	client_tick = RFIFOL(fd, packet_db[cmd].pos[3]);
10657 | 	sex = RFIFOB(fd, packet_db[cmd].pos[4]);
10658 | 
10659 | 	err = clif_parse_WantToConnection_sub(fd);
10660 | 
10661 | 	if( err ){ // connection rejected
10662 | 		ShowInfo("clif_parse: Disconnecting session #%d with unknown connect packet 0x%04x(length:%d)%s\n", fd, cmd, RFIFOREST(fd), (
10663 | 				err == 1 ? "." :
10664 | 				err == 2 ? ", possibly for having an invalid account_id." :
10665 | 				err == 3 ? ", possibly for having an invalid char_id." :
10666 | 				/* Uncomment when checks are added in clif_parse_WantToConnection_sub. [FlavioJS]
10667 | 				err == 4 ? ", possibly for having an invalid login_id1." :
10668 | 				err == 5 ? ", possibly for having an invalid client_tick." :
10669 | 				*/
10670 | 				err == 6 ? ", possibly for having an invalid sex." :
10671 | 				". ERROR invalid error code"));
10672 | 		
10673 | 		WFIFOHEAD(fd,packet_len(0x6a));
10674 | 		WFIFOW(fd,0) = 0x6a;
10675 | 		WFIFOB(fd,2) = err;
10676 | 		WFIFOSET(fd,packet_len(0x6a));
10677 | 
10678 | #ifdef DUMP_INVALID_PACKET
10679 | 		ShowDump(RFIFOP(fd, 0), RFIFOREST(fd));
10680 | #endif
10681 | 
10682 | 		RFIFOSKIP(fd, RFIFOREST(fd));
10683 | 
10684 | 		set_eof(fd);
10685 | 		return;
10686 | 	}
10687 | 
10688 | 	if( !global_core->is_running() ){ // not allowed
10689 | 		clif_authfail_fd(fd,1);// server closed
10690 | 		return;
10691 | 	}
10692 | 
10693 | 	//Check for double login.
10694 | 	bl = map_id2bl(account_id);
10695 | 	if(bl && bl->type != BL_PC) {
10696 | 		ShowError("clif_parse_WantToConnection: a non-player object already has id %d, please increase the starting account number\n", account_id);
10697 | 		WFIFOHEAD(fd,packet_len(0x6a));
10698 | 		WFIFOW(fd,0) = 0x6a;
10699 | 		WFIFOB(fd,2) = 3; // Rejected by server
10700 | 		WFIFOSET(fd,packet_len(0x6a));
10701 | 		set_eof(fd);
10702 | 		return;
10703 | 	}
10704 | 
10705 | 	if (bl ||
10706 | 		((node=chrif_search(account_id)) && //An already existing node is valid only if it is for this login.
10707 | 			!(node->account_id == account_id && node->char_id == char_id && node->state == ST_LOGIN)))
10708 | 	{
10709 | 		clif_authfail_fd(fd, 8); //Still recognizes last connection
10710 | 		return;
10711 | 	}
10712 | 
10713 | 	CREATE(sd, TBL_PC, 1);
10714 | 	new(sd) map_session_data();
10715 | 	sd->fd = fd;
10716 | #ifdef PACKET_OBFUSCATION
10717 | 	sd->cryptKey = (((((clif_cryptKey[0] * clif_cryptKey[1]) + clif_cryptKey[2]) & 0xFFFFFFFF) * clif_cryptKey[1]) + clif_cryptKey[2]) & 0xFFFFFFFF;
10718 | #endif
10719 | 	session[fd]->session_data = sd;
10720 | 
10721 | 	pc_setnewpc(sd, account_id, char_id, login_id1, client_tick, sex, fd);
10722 | 
10723 | #if PACKETVER < 20070521
10724 | 	WFIFOHEAD(fd,4);
10725 | 	WFIFOL(fd,0) = sd->id;
10726 | 	WFIFOSET(fd,4);
10727 | #else
10728 | 	WFIFOHEAD(fd,packet_len(0x283));
10729 | 	WFIFOW(fd,0) = 0x283;
10730 | 	WFIFOL(fd,2) = sd->id;
10731 | 	WFIFOSET(fd,packet_len(0x283));
10732 | #endif
10733 | 
10734 | 	chrif_authreq(sd,false);
10735 | }
```
### rAthena clif_parse_WantToConnection_sub
```cpp
10607 | static int32 clif_parse_WantToConnection_sub(int32 fd)
10608 | {
10609 | 	int32 value; //Value is used to temporarily store account/char_id/sex
10610 | 
10611 | 	//By default, start searching on the default one.
10612 | 	uint16 cmd = RFIFOW(fd, 0);
10613 | 	int16 packet_len;
10614 | 
10615 | 	packet_len = static_cast<decltype(packet_len)>( RFIFOREST( fd ) );
10616 | 
10617 | 	// FIXME: If the packet is not received at once, this will FAIL.
10618 | 	// Figure out, when it happens, that only part of the packet is
10619 | 	// received, or fix the function to be able to deal with that
10620 | 	// case.
10621 | 	if( packet_len != packet_db[cmd].len )
10622 | 		return 1; /* wrong length */
10623 | 	else if( (value=(int32)RFIFOL(fd, packet_db[cmd].pos[0])) < START_ACCOUNT_NUM || value > END_ACCOUNT_NUM )
10624 | 		return 2; /* invalid account_id */
10625 | 	else if( (value=(int32)RFIFOL(fd, packet_db[cmd].pos[1])) <= 0 )
10626 | 		return 3; /* invalid char_id */
10627 | 	/*                   RFIFOL(fd, packet_db[cmd].pos[2]) - don't care about login_id1 */
10628 | 	/*                   RFIFOL(fd, packet_db[cmd].pos[3]) - don't care about client_tick */
10629 | 	else if( (value=(int32)RFIFOB(fd, packet_db[cmd].pos[4])) != 0 && value != 1 )
10630 | 		return 6; /* invalid sex */
10631 | 	else
10632 | 		return 0;
10633 | }
```
