namespace Athena.Net.MapServer.Net;

public static class PacketConstants
{
    public const int PacketVer = 20220406;
    public const int NameLength = 24;
    public const int MapNameLength = 16;

    public const short MapLogin = 0x2af8;
    public const short MapLoginAck = 0x2af9;
    public const short MapSendMaps = 0x2afa;
    public const short MapAuthRequest = 0x2b26;
    public const short MapAuthOk = 0x2afd;
    public const short MapAuthFail = 0x2b27;
    public const short MapSavePosition = 0x2b28;
    public const short MapQuestStateRequest = 0x2b29;
    public const short MapQuestStateResponse = 0x2b2a;
    public const short MapSavePointRequest = 0x2b2b;
    public const short MapSavePointResponse = 0x2b2c;
    public const short MapGameplayStateGetRequest = 0x2b2d;
    public const short MapGameplayStateGetResponse = 0x2b2e;
    public const short MapGameplayStateUpdateRequest = 0x2b2f;
    public const short MapGameplayStateUpdateResponse = 0x2b30;
    public const short MapInventoryAddRequest = 0x2b31;
    public const short MapInventoryAddResponse = 0x2b32;
    // Renamed from MapEquipmentGet*: this reads every persisted CharInventory row for the
    // character, not just the right-hand slot (see CharacterInventorySnapshot).
    public const short MapInventoryListGetRequest = 0x2b33;
    public const short MapInventoryListGetResponse = 0x2b34;
    public const short MapInventoryEquipUpdateRequest = 0x2b35;
    public const short MapInventoryEquipUpdateResponse = 0x2b36;
    // Consumes `amount` from the row at the given authoritative SlotIndex (pinned pc_delitem,
    // pc.cpp:6103-6128) - used by item-use (First Aid Box etc.), not itemId-targeted like
    // MapInventoryAddRequest, since consumption targets a specific already-resolved row, never
    // "find or create a stack of this item".
    public const short MapInventoryConsumeRequest = 0x2b37;
    public const short MapInventoryConsumeResponse = 0x2b38;

    public const short CzEnter = 0x72;
    public const short CzEnter2 = 0x436;
    public const short CzNotifyActorInit = 0x7d;
    public const short CzClientVersion = 0x44a;
    public const short CzPingLive = 0x0b1c;
    public const short IroCzMapAuth = 0x0c1f;
    public const int IroCzMapAuthLength = 1001;
    public const short IroCzPostEnter0360 = 0x0360;
    public const int IroCzPostEnter0360Length = 7;
    public const short IroCzPostEnter08c9 = 0x08c9;
    public const int IroCzPostEnter08c9Length = 3;
    public const short IroCzRequestMove = 0x035f;
    public const int IroCzRequestMoveLength = 6;
    public const short IroCzActorInfoRequest = 0x0368;
    public const int IroCzActorInfoRequestLength = 7;
    public const short IroCzChangeDirection = 0x0361;
    public const int IroCzChangeDirectionLength = 6;
    public const short IroCzNpcInteraction = 0x0090;
    public const int IroCzNpcInteractionLength = 8;
    public const short IroCzNpcNext = 0x00b9;
    public const int IroCzNpcNextLength = 7;
    public const short IroCzNpcClose = 0x0146;
    public const int IroCzNpcCloseLength = 7;
    public const short IroCzNpcSelection = 0x00b8;
    public const int IroCzNpcSelectionLength = 8;
    // Verified kill-poring-heal-jobup capture: clif_parse_ActionRequest (clif.cpp:11818),
    // pinned generic length 7 (clif_packetdb.hpp:1149/1222), iRO adds one opaque trailing
    // byte matching the established pattern (0x0360/0x0368/0x0361/0x0090 etc).
    public const short IroCzAttackRequest = 0x0437;
    public const int IroCzAttackRequestLength = 8;

    // LIVE-VERIFIED via targeted diagnostic capture (see ai/map-server.md "Item-use request"):
    // observed bytes A7 00 04 00 80 84 1E 00 D2 for a real stock-iRO item-use interaction -
    // opcode.W(2) index.W(2) accountId.L(4) + one opaque trailing byte(1) = 9. The accountId
    // field exactly matched the authenticated session's account, confirming field identity.
    // Pinned rAthena's generic clif_packetdb.hpp table is ambiguous for 0x00A7 across PACKETVER
    // branches (clif_parse_UseItem/SolveCharName/UseSkillToPos/WalkToXY in different historical
    // branches) - per this project's evidence-priority rule, the live capture wins. Matches the
    // same "+1 opaque trailing byte beyond pinned generic length" pattern already proven for
    // attack/equip/unequip/movement/NPC packets.
    public const short IroCzUseItem = 0x00a7;
    public const int IroCzUseItemLength = 9;

    // ZC_USE_ITEM_ACK2 (clif.cpp:4468-4497, packets_struct.hpp:2577-2589) - pinned-source
    // layout for the current PACKETVER_RE_NUM >= 20180704 branch; not yet independently
    // capture-verified on the response side (see IroUseItemPackets.BuildUseItemAck).
    public const short ZcUseItemAck = 0x01c8;
    public const int ZcUseItemAckLength = 15;

    public const short ZcAcceptEnter = 0x2eb;
    public const short ZcNotifyPlayerMove = 0x0087;
    public const short ZcNpcAckMapMove = 0x0091;
    public const short ZcNotifyStandEntry = 0x09ff;
    public const short ZcNpcMessage = 0x00b4;
    public const short ZcNpcNext = 0x00b5;
    public const short ZcNpcClose = 0x00b6;
    public const short ZcNpcMenu = 0x00b7;
    public const short ZcShowImage = 0x01b3;
    public const short ZcParameterChange = 0x00b0;
    public const short ZcLongLongParameterChange = 0x0acb;
    public const short ZcRefuseEnter = 0x74;
    public const short ZcNotifyActorInit = 0x0b1b;
    public const short ZcPingLive = 0x0b1d;
    public const short ZcMsgStateChange3 = 0x0983;
    public const short ZcMsgStateChange = 0x0196;
    public const short ZcUseSkill = 0x09cb;
    public const short ZcCoupleStatus = 0x0141;
    // Verified kill-poring-heal-jobup capture, frame 566: clif_set_unit_walking
    // (clif.cpp:1369), object type 5 (NPC_MOB_TYPE) for a real monster.
    public const short ZcNotifyMoveEntry = 0x09fd;
    // Verified capture frame 620/659: ZC_NOTIFY_ACT3 (clif.cpp:5220), exact 34-byte
    // match: srcId.L dstId.L tick.L srcSpeed.L dstSpeed.L damage.L isSpDamage.B div.W type.B damage2.L
    public const short ZcNotifyAct3 = 0x08c8;
    public const int ZcNotifyAct3Length = 34;
    // Verified capture frame 674: ZC_STOPMOVE (clif.cpp:2204): id.L x.W y.W
    public const short ZcStopMove = 0x0088;
    public const int ZcStopMoveLength = 10;
    // Verified capture frame 674 (coalesced after 0x0088): ZC_NOTIFY_ACT2 (clif.cpp:5219),
    // srcId.L dstId.L tick.L srcSpeed.L dstSpeed.L damage.L div.W type.B damage2.L
    public const short ZcNotifyAct2 = 0x02e1;
    public const int ZcNotifyAct2Length = 33;
    // Verified capture frame 694: ZC_NOTIFY_VANISH (clif.cpp:945): id.L type.B
    // type=1 is explicitly "died" per pinned source comment.
    public const short ZcNotifyVanish = 0x0080;
    public const int ZcNotifyVanishLength = 7;
    public const byte ZcNotifyVanishReasonDied = 1;
    // Verified capture frame 699: exact 70-byte match to pinned PACKET_ZC_ITEM_PICKUP_ACK
    // (packets_struct.hpp:540) under the pinned RE PACKETVER branch.
    public const short ZcItemPickupAck = 0x0b41;
    public const int ZcItemPickupAckLength = 70;
    public const byte ZcItemPickupResultSuccess = 0;
    // PINNED-SOURCE-BACKED, NOT capture-verified (no stock-iRO capture of this packet has been
    // independently obtained yet - see IroCombatDistancePackets.BuildAttackFailureForDistance's
    // own doc comment). Struct PACKET_ZC_ATTACK_FAILURE_FOR_DISTANCE (packets_struct.hpp:5419-
    // 5426): PacketType.W targetAID.L targetXPos.W targetYPos.W xPos.W yPos.W currentAttRange.W =
    // 2+4+2+2+2+2+2 = 16 bytes, fixed length (no name/variable-length trailer). Header
    // DEFINE_PACKET_HEADER(ZC_ATTACK_FAILURE_FOR_DISTANCE, 0x0139).
    public const short ZcAttackFailureForDistance = 0x0139;
    public const int ZcAttackFailureForDistanceLength = 16;
    // Traced pinned rAthena: sendLookType (packets_struct.hpp:317, PACKETVER >= 4, pinned build
    // satisfies this). PACKET_ZC_SPRITE_CHANGE (packets_struct.hpp:2591), wide-field variant
    // (PACKETVER_RE_NUM >= 20180704, pinned build satisfies this): packetType.W AID.L type.B
    // val.L val2.L = 15 bytes. Sent inside clif_parse_LoadEndAck (0x007D handler, clif.cpp:10771)
    // via clif_changelook(sd, LOOK_WEAPON, ...) with target=AREA (includes self), BEFORE the
    // AREA_WOS spawn/idle broadcast - so this is what makes the local client see its own
    // equipped weapon at initial map load, not the spawn packet's embedded weapon field.
    public const short ZcSpriteChange = 0x01d7;
    public const int ZcSpriteChangeLength = 15;
    // Pinned enum _look (map.hpp:594-596): LOOK_BASE=0, LOOK_HAIR=1, LOOK_WEAPON=2.
    public const byte ZcSpriteChangeTypeWeapon = 2;

    // Pinned rAthena CZ_REQ_WEAR_EQUIP_V5 (packets.hpp:1502-1509, gated PACKETVER >= 20120925):
    // packetType.W index.W position.L = 8 bytes. VERIFIED STOCK-iRO CAPTURE DIVERGES: current
    // iRO sends 9 bytes (frames 388/449: "98 09 02 00 02 00 00 00 5B" /
    // "98 09 03 00 10 00 00 00 88") - one trailing opaque byte beyond the pinned shape. Per
    // evidence-priority rules, the capture overrides pinned source for this iRO-specific wire
    // length. The 9th byte's semantics are unverified and intentionally left opaque/uninterpreted
    // - do not invent a checksum/token/anti-cheat meaning for it.
    public const short IroCzReqWearEquip = 0x0998;
    public const int IroCzReqWearEquipLength = 9;
    // ZC_ACK_WEAR_EQUIP_V5 (packets_struct.hpp:1268-1274, gated PACKETVER_RE_NUM >= 20121107,
    // pinned build satisfies this): PacketType.W index.W wearLocation.L wItemSpriteNumber.W
    // result.B = 11 bytes.
    public const short IroZcReqWearEquipAck = 0x0999;
    public const int IroZcReqWearEquipAckLength = 11;
    // Pinned clif_equipitemack doc comment (clif.cpp:4301-4303) + enum clif_equipitemack_flag
    // (clif.hpp:522-533, gated PACKETVER_RE_NUM >= 20121107, pinned build satisfies this):
    // OK=0, FAILLEVEL=1, FAIL=2. NOT inverted for the equip ack (only the unequip ack is).
    public const byte EquipAckResultOk = 0;
    public const byte EquipAckResultFailLevel = 1;
    public const byte EquipAckResultFail = 2;

    // Pinned rAthena CZ_REQ_TAKEOFF_EQUIP (clif_packetdb.hpp:59): unconditionally 0x00AB,
    // 4 bytes, not PACKETVER-gated. packetType.W index.W. VERIFIED STOCK-iRO CAPTURE DIVERGES:
    // current iRO sends 5 bytes (frames 370/395: "AB 00 02 00 4F" / "AB 00 03 00 85") - one
    // trailing opaque byte beyond the pinned shape, same divergence pattern as 0x0998. Per
    // evidence-priority rules, the capture overrides pinned source for this iRO-specific wire
    // length. The 5th byte's semantics are unverified and intentionally left opaque/
    // uninterpreted.
    public const short IroCzReqTakeoffEquip = 0x00ab;
    public const int IroCzReqTakeoffEquipLength = 5;
    // ZC_ACK_TAKEOFF_EQUIP_V5 (packets.hpp:1006-1013, gated PACKETVER >= 20130000, pinned
    // build satisfies this): packetType.W index.W wearLocation.L flag.B = 9 bytes.
    public const short IroZcReqTakeoffEquipAck = 0x099a;
    public const int IroZcReqTakeoffEquipAckLength = 9;
    // Pinned clif_unequipitemack (clif.cpp:4338-4341): `success = !success` for
    // PACKETVER >= 20110824 (pinned build satisfies this) - the wire flag is INVERTED
    // relative to the equip ack: 0 = success, 1 = failure (opposite of EquipAckResultOk/Fail).
    public const byte UnequipAckFlagSuccess = 0;
    public const byte UnequipAckFlagFailure = 1;
}
