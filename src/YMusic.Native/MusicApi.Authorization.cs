using System.Net.Http;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;

namespace YMusic.Native;

internal sealed partial class MusicApi
{
    // Device Flow without scope inherits every permission of the registered client.
    // Keep this list explicit; never retry a rejected scope with a broader request.
    internal const string RequiredScopes = "music:content music:read music:write";

    internal static bool HasPlayerScopes(Session session) => IsPlayerScope(session.Scope);

    private static bool IsPlayerScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope) || scope.Length > 256 || scope.Any(char.IsControl)) return false;
        var names = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return names.Length == 3 && names.Distinct(StringComparer.Ordinal).Count() == 3
            && names.ToHashSet(StringComparer.Ordinal).SetEquals(RequiredScopes.Split(' '));
    }

    private static void RequirePlayerScopes(Session session)
    {
        if (!HasPlayerScopes(session)) throw ScopeFailure();
        if (!IsOAuthValue(session.AccessToken)) throw new MusicException("Сохранённый токен входа повреждён. Войдите заново.", true);
    }

    internal static bool IsOAuthValue([NotNullWhen(true)] string? value) =>
        !string.IsNullOrEmpty(value) && !value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character));

    private static string GrantedPlayerScope(JsonElement root)
    {
        // Yandex documents scope as optional when the requested rights were granted.
        // A reported grant must contain exactly our requested music permissions.
        if (root.TryGetProperty("scope", out var scope) && !IsPlayerScope(JsonString(scope)))
            throw ScopeFailure();
        return RequiredScopes;
    }

    private static MusicException ScopeFailure() => new(
        "Нужен новый вход с доступом только к музыке. Прежний или неподходящий набор разрешений не используется.", true);

    private static MusicException RevokeFailure() => new(
        "Не удалось отозвать прежний доступ. Повторите вход позже. После ручного отзыва доступа устройства Yamuse в Яндекс ID закройте приложение, удалите session.dpapi из папки данных и войдите заново.", true);

    internal async Task RevokeSessionAsync(Session session, CancellationToken cancellationToken)
    {
        if (!IsOAuthValue(session.AccessToken))
            throw RevokeFailure();
        using var timeout = RequestTimeout(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth.yandex.ru/revoke_token")
        {
            Content = Form(("access_token", session.AccessToken), ("client_id", ClientId), ("client_secret", PublicClientSecret))
        };
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw RevokeFailure();
            using var json = await ReadJsonAsync(response, timeout.Token).ConfigureAwait(false);
            if (JsonString(json.RootElement, "status") != "ok" || json.RootElement.TryGetProperty("error", out _))
                throw RevokeFailure();
        }
        catch (MusicException error) when (!error.Authentication) { throw RevokeFailure(); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw RevokeFailure(); }
        catch (Exception error) when (error is HttpRequestException or IOException) { throw RevokeFailure(); }
    }
}
