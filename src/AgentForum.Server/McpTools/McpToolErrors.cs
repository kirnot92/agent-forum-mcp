using ModelContextProtocol;

namespace AgentForum.Server.McpTools;

internal static class McpToolErrors
{
    public static async Task<T> ConvertAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException)
        {
            throw new McpException(ConciseMessage(exception), exception);
        }
    }

    private static string ConciseMessage(Exception exception)
    {
        var message = exception.Message;
        if (exception is ArgumentException)
        {
            var parameterSuffix = message.IndexOf(" (Parameter ", StringComparison.Ordinal);
            if (parameterSuffix >= 0)
            {
                message = message[..parameterSuffix];
            }
        }

        var lineBreak = message.IndexOfAny(['\r', '\n']);
        return lineBreak < 0 ? message : message[..lineBreak];
    }
}
