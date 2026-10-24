using System.Net.Http;
using System.Text.Json;

namespace YMusic.Native;

internal sealed partial class MusicApi
{
    // Official desktop 5.124.1 AccountResource.about(): account/about returns uid and avatarId.
    // Avatar is optional. Only the account confirmed by account/status may supply its portrait.
    private async Task<string?> GetOwnAvatarUriAsync(Session session, string expectedUid, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            using var json = await ApiAsync(HttpMethod.Get, "/account/about", session, null, timeout.Token).ConfigureAwait(false);
            var profile = Result(json);
            if (profile.ValueKind != JsonValueKind.Object || Text(profile, "uid") != expectedUid) return null;
            return ProfileAvatarUri(profile);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (MusicException error) when (!error.Authentication) { return null; }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException) { return null; }
    }

    private static string? ProfileAvatarUri(JsonElement profile)
    {
        if (profile.ValueKind != JsonValueKind.Object || !profile.TryGetProperty("avatarId", out var avatar)
            || avatar.ValueKind != JsonValueKind.String || avatar.GetString() is not { Length: > 0 and <= 160 } id) return null;
        var parts = id.Split('/');
        if (parts.Length is < 1 or > 2 || parts[0].Length is < 1 or > 20 || !parts[0].All(char.IsAsciiDigit)) return null;
        if (parts.Length == 2 && (parts[1].Length is < 1 or > 128
            || parts[1].Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))) return null;
        // The server-provided ID is never inferred from a UID, login or display name.
        return "https://avatars.mds.yandex.net/get-yapic/" + id + "/islands-retina-middle";
    }
}
