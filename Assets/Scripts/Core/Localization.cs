using System.Collections.Generic;
using UnityEngine;

public enum Language
{
    Japanese,
    English
}

/// <summary>
/// 軽量なローカライズ。文字列はこのクラス内の辞書にキーごと日本語/英語で持ち、Localization.Get("key") で取得する。
/// 言語はPlayerPrefsに保存する。
/// </summary>
public static class Localization
{
    private const string PrefsKey = "Language";

    private static Language _current;
    private static bool _initialized;

    public static Language Current
    {
        get
        {
            EnsureInitialized();
            return _current;
        }
        set
        {
            _current = value;
            PlayerPrefs.SetInt(PrefsKey, (int)value);
            PlayerPrefs.Save();
        }
    }

    private static void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;

        if (PlayerPrefs.HasKey(PrefsKey))
        {
            _current = (Language)PlayerPrefs.GetInt(PrefsKey);
            return;
        }

        _current = Language.Japanese;
    }

    /// <summary>キーに対応する現在言語の文字列を返す。未登録キーはキーそのものを返す</summary>
    public static string Get(string key)
    {
        EnsureInitialized();
        if (!Table.TryGetValue(key, out var entry)) return key;

        return _current == Language.Japanese ? entry.ja : entry.en;
    }

    /// <summary>Get(key)の結果をstring.Formatに渡す</summary>
    public static string Format(string key, params object[] args)
    {
        return string.Format(Get(key), args);
    }

    private readonly struct Entry
    {
        public readonly string ja;
        public readonly string en;

        public Entry(string ja, string en)
        {
            this.ja = ja;
            this.en = en;
        }
    }

    private static readonly Dictionary<string, Entry> Table = new Dictionary<string, Entry>
    {
        // タイトル・設定画面
        ["ui.loading"] = new Entry("読み込み中...", "Loading..."),
        ["ui.nickname"] = new Entry("ニックネーム:", "Nickname:"),
        ["ui.nicknameChip"] = new Entry("名前: {0}", "Name: {0}"),
        ["ui.retry"] = new Entry("再試行", "Retry"),
        ["ui.play"] = new Entry("プレイ", "PLAY"),
        ["ui.joinByCode"] = new Entry("ロビーコードで参加", "Join with Lobby Code"),
        ["ui.join"] = new Entry("参加する", "Join"),
        ["ui.disconnect"] = new Entry("切断する", "Disconnect"),
        ["ui.settingsTitle"] = new Entry("設定", "Settings"),
        ["ui.close"] = new Entry("閉じる", "Close"),
        ["ui.quit"] = new Entry("終了", "Quit"),
        ["ui.outlineOn"] = new Entry("アウトライン: ON", "Outline: ON"),
        ["ui.outlineOff"] = new Entry("アウトライン: OFF", "Outline: OFF"),
        ["ui.outlineColor"] = new Entry("色を変更", "Change Color"),
        ["ui.damageDisplayOn"] = new Entry("ダメージ表示: ON", "Damage Display: ON"),
        ["ui.damageDisplayOff"] = new Entry("ダメージ表示: OFF", "Damage Display: OFF"),
        ["ui.languageLabel"] = new Entry("言語: {0}", "Language: {0}"),
        ["ui.settingsCategoryDisplay"] = new Entry("表示", "Display"),
        ["ui.settingsCategoryLanguage"] = new Entry("言語", "Language"),
        ["ui.settingsHeaderDisplay"] = new Entry("表示設定", "Display Settings"),
        ["ui.settingsHeaderLanguage"] = new Entry("言語設定", "Language Settings"),
        ["ui.settingsCategoryControls"] = new Entry("操作", "Controls"),
        ["ui.settingsHeaderControls"] = new Entry("操作設定", "Controls Settings"),
        ["ui.sensitivityLabel"] = new Entry("視点感度: {0:0.00}倍", "Look Sensitivity: {0:0.00}x"),
        ["ui.settingsCategoryGraphics"] = new Entry("画質", "Graphics"),
        ["touch.throw"] = new Entry("投げる", "Throw"),
        ["touch.throwRelease"] = new Entry("離して投げる", "Release"),
        ["touch.shoot"] = new Entry("撃つ", "Shoot"),
        ["touch.pickup"] = new Entry("拾う", "Pick up"),
        ["ui.settingsCategorySound"] = new Entry("サウンド", "Sound"),
        ["ui.settingsHeaderSound"] = new Entry("サウンド設定", "Sound Settings"),
        ["ui.volumeMaster"] = new Entry("全体: {0}%", "Master: {0}%"),
        ["ui.volumeBgm"] = new Entry("BGM: {0}%", "Music: {0}%"),
        ["ui.volumeSfx"] = new Entry("効果音: {0}%", "SFX: {0}%"),
        ["ui.settingsHeaderGraphics"] = new Entry("画質設定", "Graphics Settings"),
        ["ui.graphicsQualityLabel"] = new Entry("画質: {0}", "Quality: {0}"),
        ["ui.graphicsLow"] = new Entry("低(軽い)", "Low (fast)"),
        ["ui.graphicsMedium"] = new Entry("中", "Medium"),
        ["ui.graphicsHigh"] = new Entry("高(きれい)", "High (pretty)"),
        ["ui.graphicsHintLow"] = new Entry(
            "解像度↓・影なし・遠くの景色/アイテムを省略・30fps上限",
            "Lower resolution, no shadows, skip far scenery/items, 30fps cap"),
        ["ui.graphicsHintMedium"] = new Entry(
            "解像度やや↓・影は近くだけ・遠くのアイテムを省略",
            "Slightly lower resolution, near shadows only, skip far items"),
        ["ui.graphicsHintHigh"] = new Entry("本来の画質(PC向け)", "Original quality (for PC)"),
        ["ui.fpsOn"] = new Entry("FPS表示: ON", "Show FPS: ON"),
        ["ui.fpsOff"] = new Entry("FPS表示: OFF", "Show FPS: OFF"),

        // タイトル画面の文言とTIPS(1行に収まる長さ。キーを増減したらLobbyUI.TitleTipKeysも合わせる)
        ["ui.titleSubtitle"] = new Entry("B O M B   R O Y A L E", "B O M B   R O Y A L E"),
        ["ui.titleTagline"] = new Entry("武器を選び、街を壊し、最後の1人になれ。", "Pick your weapon, tear down the town, be the last one standing."),
        ["ui.tipsLabel"] = new Entry("TIPS", "TIPS"),
        ["ui.tip1"] = new Entry("街の建物は爆発で崩せる。物陰も過信は禁物!", "Explosions can bring buildings down — don't trust cover too much!"),
        ["ui.tip2"] = new Entry("ロケットは持ち替えるとリロードが最初からやり直し!", "Switching away from the Rocket Launcher restarts its reload!"),
        ["ui.tip3"] = new Entry("爆発物は誘爆する。地面に落ちている爆弾も巻き込める!", "Explosives set each other off — even bombs lying on the ground!"),
        ["ui.tip4"] = new Entry("ブラスターを数発当てれば、爆弾を撃って起爆できる!", "Hit a bomb with a few Blaster shots to set it off early!"),
        ["ui.tip5"] = new Entry("自爆ダメージは軽減される。自爆ジャンプも悪くない。", "Your own explosives hurt you less — a blast jump isn't a bad idea."),
        ["ui.tip6"] = new Entry("武器スロットは4つ。アイコンを枠外へドラッグで捨てられる!", "There are 4 weapon slots — drag an icon off the bar to drop it!"),
        ["ui.tip7"] = new Entry("ブラスターは撃ちすぎるとオーバーヒートして撃てなくなる。", "The Blaster overheats if you keep firing — you'll have to wait it out."),
        ["ui.tip8"] = new Entry("ショックウェーブは建物を壊せないが、敵を大きく吹き飛ばす!", "The Shockwave Gun can't damage buildings, but it launches enemies!"),
        ["ui.tip9"] = new Entry("火炎爆弾は爆発地点を数秒間燃やし続ける。", "Fire Bombs leave the ground burning for a few seconds."),
        ["ui.tip10"] = new Entry("凍結爆弾は範囲内の全員を鈍足に。自分も例外じゃない!", "Freeze Bombs slow everyone in range — including you!"),
        ["ui.tip11"] = new Entry("炸裂爆弾は爆発の瞬間、子爆弾をばらまく!", "Split Bombs scatter mini-bombs the moment they explode!"),
        ["ui.tip12"] = new Entry("時間切れは生き残りの中でキル数が最多の人の勝ち!", "If time runs out, the survivor with the most kills wins!"),

        // 武器名
        ["weapon.blaster"] = new Entry("ブラスター", "Blaster"),
        ["weapon.bomb"] = new Entry("グレネード", "Grenade"),
        ["weapon.rocketLauncher"] = new Entry("ロケットランチャー", "Rocket Launcher"),
        ["weapon.shockwaveGun"] = new Entry("ショックウェーブガン", "Shockwave Gun"),

        ["lobby.title"] = new Entry("ロビー", "Lobby"),
        ["lobby.connected"] = new Entry("接続中: {0}人", "Connected: {0}"),
        ["lobby.start"] = new Entry("ゲーム開始", "Start Game"),
        ["lobby.waitingHost"] = new Entry("ホストの開始を待っています…", "Waiting for host to start…"),

        ["gameover.returnLobby"] = new Entry("ロビーに戻る", "Return to Lobby"),

        // 観戦
        ["spectate.watching"] = new Entry("観戦中: {0}", "Watching: {0}"),
        ["spectate.cycleHint"] = new Entry("拾得ボタンで次の生存者へ切り替え", "Tap pickup to switch target"),

        // 接続状況
        ["relay.ready"] = new Entry("準備完了", "Ready"),
        ["relay.reconnectCooldown"] = new Entry(
            "前回の切断から少し間を空けています...",
            "Waiting a moment after the last disconnect..."),
        ["relay.initFailed"] = new Entry("初期化に失敗しました", "Failed to initialize"),
        ["relay.offline"] = new Entry("接続が切れました", "Connection lost"),
        ["relay.creatingLobby"] = new Entry("ロビーを作成中...", "Creating lobby..."),
        ["relay.hostStarted"] = new Entry("ホスト開始！ ロビーコード: {0}", "Host started! Lobby code: {0}"),
        ["relay.hostFailed"] = new Entry("ホストに失敗しました", "Failed to host"),
        ["relay.enterCode"] = new Entry("ロビーコードを入力してください", "Please enter a lobby code"),
        ["relay.joining"] = new Entry("ロビーに参加中...", "Joining lobby..."),
        ["relay.joinSuccess"] = new Entry("参加成功！", "Joined successfully!"),
        ["relay.joinFailed"] = new Entry("参加に失敗しました（コードを確認してください）", "Failed to join (check the code)"),
        ["relay.searching"] = new Entry("対戦相手を探しています...", "Searching for opponents..."),
        ["relay.noOpenLobby"] = new Entry("空いているロビーがありません。ホストしてください。", "No open lobbies. Please host a game."),
        ["relay.quickJoinFailed"] = new Entry("クイックジョインに失敗しました", "Quick join failed"),
        ["relay.disconnected"] = new Entry("切断しました", "Disconnected"),
        ["relay.disconnectedUnexpectedly"] = new Entry(
            "接続が切断されました(試合が既に進行中の可能性があります)",
            "Disconnected unexpectedly (the match may already be in progress)"),
    };
}
