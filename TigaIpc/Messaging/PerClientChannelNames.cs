namespace TigaIpc.Messaging;

public static class PerClientChannelNames
{
    public static string GetRequestChannelName(string baseName, string clientId)
    {
        if (string.IsNullOrWhiteSpace(baseName))
        {
            throw new ArgumentException("Base name must be provided.", nameof(baseName));
        }

        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new ArgumentException("Client id must be provided.", nameof(clientId));
        }

        return $"{baseName}.req.{clientId}";
    }

    public static string GetResponseChannelName(string baseName, string clientId)
    {
        if (string.IsNullOrWhiteSpace(baseName))
        {
            throw new ArgumentException("Base name must be provided.", nameof(baseName));
        }

        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new ArgumentException("Client id must be provided.", nameof(clientId));
        }

        return $"{baseName}.resp.{clientId}";
    }
}
