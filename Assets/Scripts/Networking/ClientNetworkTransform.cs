using Unity.Netcode.Components;

/// <summary>
/// Owner(操作している本人)を権威にするNetworkTransform。プレイヤーの移動同期に使う。
/// </summary>
public class ClientNetworkTransform : NetworkTransform
{
    protected override bool OnIsServerAuthoritative()
    {
        return false; // false = Owner（クライアント）権威
    }
}
