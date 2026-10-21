/// <summary>
/// 1フレーム分の入力をまとめた構造体。PlayerControllerは入力APIを直接読まずこれだけを見て動く
/// (人間はTouchInputProvider、Botは別の作り方で埋める)。
/// </summary>
public struct PlayerInputFrame
{
    /// <summary>左スティック。-1〜1、カメラ基準の左右</summary>
    public float horizontal;
    /// <summary>左スティック。-1〜1、カメラ基準の前後</summary>
    public float vertical;
    /// <summary>このフレームに回転させたいヨー角(度、感度反映済み)</summary>
    public float lookYawDelta;
    /// <summary>このフレームに回転させたいピッチ角(度、感度反映済み)</summary>
    public float lookPitchDelta;

    /// <summary>攻撃/構えボタンを押し続けているか</summary>
    public bool wantsFirePrimaryHeld;
    /// <summary>武器切り替え先のインデックス。-1 = 切り替えない</summary>
    public int weaponSwitchIndexOrNegativeOne;
    /// <summary>拾得ボタンを押した瞬間</summary>
    public bool wantsPickupDown;
    /// <summary>装備中の武器を足元に落とすボタンを押した瞬間</summary>
    public bool wantsDropDown;
    /// <summary>ジャンプボタンを押した瞬間のみtrue</summary>
    public bool wantsJumpDown;

    public static PlayerInputFrame Empty => new PlayerInputFrame { weaponSwitchIndexOrNegativeOne = -1 };
}
