namespace TigaIpc.Messaging;

public static class PerClientChannelNames
{
    public static string GetRequestChannelName(string channelName, string clientId)
    {
        if (string.IsNullOrWhiteSpace(channelName))
        {
            throw new ArgumentException("Channel name must be provided.", nameof(channelName));
        }

        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new ArgumentException("Client id must be provided.", nameof(clientId));
        }

        return $"{channelName}.req.{clientId}";
    }

    public static string GetResponseChannelName(string channelName, string clientId)
    {
        if (string.IsNullOrWhiteSpace(channelName))
        {
            throw new ArgumentException("Channel name must be provided.", nameof(channelName));
        }

        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new ArgumentException("Client id must be provided.", nameof(clientId));
        }

        return $"{channelName}.resp.{clientId}";
    }
}
