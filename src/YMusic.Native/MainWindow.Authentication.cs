using System.Security.Cryptography;
using System.Text.Json;

namespace YMusic.Native;

public partial class MainWindow
{
    private async Task RevokeLegacyLoginAsync(Session saved, CancellationToken cancellationToken)
    {
        SetStatus("Отзываем прежний доступ перед входом с музыкальными разрешениями…");
        await _api.RevokeSessionAsync(saved, cancellationToken);
        // Clear only after a confirmed revoke. A failed/cancelled request retains
        // the DPAPI record for a safe retry, never for a Music API call.
        _store.Clear();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task PreparePlayerLoginAsync(CancellationToken cancellationToken)
    {
        Session? previous;
        try { previous = _store.Load(); }
        // An unreadable record can still represent an active broad grant. Do
        // not overwrite the only local copy without a confirmed device revoke.
        catch (Exception error) when (error is CryptographicException or JsonException)
        {
            throw new MusicException("Сохранённый вход повреждён. Отзовите прежний доступ устройства Yamuse в Яндекс ID и удалите повреждённую сохранённую сессию перед новым входом.", true);
        }
        if (previous is not null && !MusicApi.HasPlayerScopes(previous))
            await RevokeLegacyLoginAsync(previous, cancellationToken);
    }

    private void ShowPlayerLoginRequired()
    {
        LoginTitle.Text = "Нужно войти заново";
        LoginDescription.Text = "Прежний доступ отозван. Новый вход запросит только каталог, чтение и изменение вашей музыкальной коллекции.";
        SetStatus("Разрешения сокращены до музыкальных. Нажмите «Войти в Яндекс».");
    }
}
