using System.Text.Json;
using PeakCan.Host.Mobile.Core.Chat;

namespace PeakCan.Host.Mobile.Platform;

/// <summary>
/// <see cref="IChatConfigStore"/> over MAUI <c>Preferences</c>: persists the
/// saved-key metadata list (vendor / api base / model / alias) as one JSON
/// array so custom vendors survive app restarts. Secrets stay in
/// <see cref="SecureStorageCredentialStore"/>.
/// </summary>
public sealed class MauiChatConfigStore : IChatConfigStore
{
    private const string PrefKey = "PeakCan.Chat.SavedKeys";
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public IReadOnlyList<SavedChatKeyMeta> Load()
    {
        var raw = Preferences.Default.Get(PrefKey, (string?)null);
        if (string.IsNullOrEmpty(raw)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<SavedChatKeyMeta>>(raw, JsonOpts) ?? [];
        }
        catch (JsonException)
        {
            // 配置损坏时按空处理（不崩溃，用户可重配）
            return [];
        }
    }

    public void Save(IReadOnlyList<SavedChatKeyMeta> keys)
        => Preferences.Default.Set(PrefKey, JsonSerializer.Serialize(keys));
}
