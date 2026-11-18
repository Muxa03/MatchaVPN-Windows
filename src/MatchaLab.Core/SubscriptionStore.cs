using System.Net;

namespace MatchaLab.Core;

public sealed class SubscriptionStore
{
    public const string KToken = "matcha.sub.token";

    private readonly ApiClient _api;
    private readonly ISecretStore _secret;

    public string? Token { get; private set; }
    public IReadOnlyList<CatalogServer> Servers { get; private set; } = Array.Empty<CatalogServer>();
    public string? SelectedServerId { get; set; }
    public string SelectedProtoId { get; set; } = "amneziaWG";
    public string? LastError { get; private set; }

    public bool HasToken => !string.IsNullOrEmpty(Token);

    public CatalogServer? SelectedServer =>
        Servers.FirstOrDefault(s => s.Id == SelectedServerId) ?? Servers.FirstOrDefault();

    public SubscriptionStore(ApiClient api, ISecretStore secret)
    {
        _api = api;
        _secret = secret;
        Token = _secret.Get(KToken);
    }

    public void SetToken(string raw)
    {
        var t = ExtractToken(raw);
        if (string.IsNullOrEmpty(t) || t == Token) return;
        Token = t;
        Servers = Array.Empty<CatalogServer>();
        _secret.Set(KToken, t);
    }

    public void SignOut()
    {
        Token = null;
        Servers = Array.Empty<CatalogServer>();
        _secret.Remove(KToken);
    }

    public async Task RefreshCatalogAsync(CancellationToken ct = default)
    {
        var token = Token;
        if (string.IsNullOrEmpty(token)) return;
        Catalog? cat;
        try { cat = await _api.GetCatalogAsync(token, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            if (Token == token)
            {
                Servers = Array.Empty<CatalogServer>();
                LastError = "Ключ недействителен или срок истёк";
            }
            return;
        }
        catch
        {
            if (Token == token) LastError = "Нет соединения с сервером - проверьте сеть";
            return;
        }
        if (Token != token) return;
        if (cat is null) { LastError = "Сервер вернул пустой каталог"; return; }
        Servers = cat.Servers.Where(s => s.Protocols.Contains("amneziaWG") || s.Protocols.Contains("hysteria2")).ToList();
        SelectedServerId = SelectedServer?.Id;
        LastError = null;
    }

    public async Task<string?> ResolveSelectedAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(Token) || SelectedServer is null) return null;
        LastError = null;
        try { return await _api.ResolveAsync(Token, SelectedServer.Id, SelectedProtoId, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException ex)
        {
            LastError = ex.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Ключ недействителен или срок истёк",
                HttpStatusCode.NotFound => "Локация или протокол больше недоступны. Обновите список серверов.",
                null => "Не удалось связаться с сервером подписок. Проверьте сеть.",
                _ => "Сервер не выдал конфигурацию для этой локации. Попробуйте ещё раз.",
            };
            return null;
        }
        catch { LastError = "Нет соединения с сервером - проверьте сеть"; return null; }
    }

    private static string ExtractToken(string s)
    {
        s = s.Trim();
        if (s.StartsWith("matcha://", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(s, UriKind.Absolute, out var uri))
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                if (kv.Length == 2 && kv[0].Equals("token", StringComparison.OrdinalIgnoreCase) && kv[1].Length > 0)
                    return Uri.UnescapeDataString(kv[1]);
            }
        }
        return s;
    }
}
