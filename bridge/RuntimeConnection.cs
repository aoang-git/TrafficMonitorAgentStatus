using System.Net;
using System.Text.Json;

namespace AgentStatus;

// The credential is deliberately excluded from ToString and every UI model.
public sealed class RuntimeConnection(Uri endpoint, string token, string epoch = "", int pid = 0)
{
    public Uri Endpoint { get; } = endpoint;
    public string Epoch { get; } = epoch;
    public int Pid { get; } = pid;
    internal string Token { get; } = token;
    public override string ToString() => Endpoint.ToString();

    public static async Task<RuntimeConnection> LoadAsync(string directory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, "private", "connection.json");
        if (!File.Exists(path)) throw new FileNotFoundException("未找到 Runtime 服务连接信息。");
        var info = new FileInfo(path);
        if (info.Length > 16 * 1024) throw new InvalidDataException("连接文件过大。");
        return Parse(await File.ReadAllTextAsync(path, cancellationToken));
    }

    private static RuntimeConnection Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("连接文件字段类型不正确。");
            if (!root.TryGetProperty("epoch", out var epochField) || !root.TryGetProperty("pid", out var pidField))
                throw new InvalidDataException("连接信息缺少新版服务实例标识，请更新或启动 Runtime 服务。");
            var url = root.GetProperty("url").GetString();
            var token = root.GetProperty("token").GetString();
            var epoch = epochField.GetString();
            var pid = pidField.GetInt32();
            if (string.IsNullOrWhiteSpace(epoch) || epoch.Length > 256 || pid <= 0) throw new InvalidDataException("连接文件缺少服务实例标识。");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "http" ||
                !IPAddress.TryParse(endpoint.DnsSafeHost, out var ip) || !IPAddress.IsLoopback(ip) ||
                endpoint.UserInfo != "" || endpoint.AbsolutePath != "/" || endpoint.Query != "" || endpoint.Fragment != "")
                throw new InvalidDataException("连接地址必须是本机 HTTP loopback 地址。");
            if (string.IsNullOrWhiteSpace(token) || token.Length > 4096 || token.Any(char.IsControl))
                throw new InvalidDataException("连接文件缺少有效凭据。");
            return new(endpoint, token, epoch, pid);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("连接文件不是有效 JSON，可能已损坏。");
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("连接信息字段缺失或类型不正确，请更新 Runtime 服务。");
        }
    }
}
