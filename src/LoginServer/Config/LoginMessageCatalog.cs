namespace Athena.Net.LoginServer.Config;

public sealed class LoginMessageCatalog
{
    private readonly Dictionary<uint, string> _messages;

    public LoginMessageCatalog(Dictionary<uint, string> messages)
    {
        _messages = messages;
    }

    public bool TryGet(uint code, out string message)
    {
        return _messages.TryGetValue(code, out message!);
    }
}
