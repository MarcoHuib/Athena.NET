namespace Athena.Net.MapServer.World;

public sealed record PlayerMovementPresence(
    ushort StartX,
    ushort StartY,
    ushort DestinationX,
    ushort DestinationY,
    uint StartTick);

// Immutable public world projection. It intentionally contains no transport,
// session, persistence-context, or private account state.
public sealed record PlayerPresence(
    uint ActorId,
    uint CharacterId,
    string CharacterName,
    string MapName,
    ushort X,
    ushort Y,
    byte Direction,
    byte HeadDirection,
    PlayerMovementPresence? Movement,
    ushort JobClass,
    byte Sex,
    ushort BaseLevel,
    ushort WalkSpeed,
    ushort HairStyle,
    ushort HairColor,
    ushort ClothesColor,
    ushort BodyStyle,
    uint WeaponAppearance,
    uint ShieldAppearance,
    ushort HeadBottomAppearance,
    ushort HeadTopAppearance,
    ushort HeadMidAppearance,
    ushort RobeAppearance,
    short Manner,
    byte Karma,
    uint Option,
    ushort Font,
    string PartyName = "",
    string GuildName = "",
    string GuildPositionName = "");

public enum PlayerSessionLifecycle
{
    Unauthenticated,
    AuthenticatedButNotWorldVisible,
    WorldVisible,
    ChangingMapOrUnregistering,
    Closed,
}
