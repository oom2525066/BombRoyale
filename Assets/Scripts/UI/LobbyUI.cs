using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// OnGUIベースの簡易UI。未接続時のタイトル画面、接続中のロビー/試合中オーバーレイ、結果画面、設定ウィンドウを描画する。
/// 接続済みのLobby/Playing中は右上の小さいオーバーレイに畳み、未接続と結果画面(RoundEnd/GameOver)のみフル表示にする。
/// </summary>
public class LobbyUI : MonoBehaviour
{
    private string _status = "";
    private string _lobbyCodeInput = "";
    private string _nameInput;
    private bool _subscribed;
    // ロビーコード参加は折りたたみトグルの奥に置く
    private bool _showJoinByCode;

    // 結果画面(GameOver)の紙吹雪の1片
    private struct ConfettiPiece
    {
        public Vector2 Position;
        public float FallSpeed;
        public float RotationSpeed;
        public float Rotation;
        public Color Color;
        public float Width;
        public float Height;
    }

    private ConfettiPiece[] _confetti;

    // ヒントテキストのLocalizationキー
    private static readonly string[] TitleTipKeys =
    {
        "ui.tip1", "ui.tip2", "ui.tip3", "ui.tip4", "ui.tip5", "ui.tip6",
        "ui.tip7", "ui.tip8", "ui.tip9", "ui.tip10", "ui.tip11", "ui.tip12",
    };

    private int _tipIndex;
    private float _tipChangeTime;
    private float _tipFadeStartTime;

    private void Update()
    {
        if (!_subscribed && RelayManager.Instance != null)
        {
            RelayManager.Instance.OnStatus += HandleStatus;
            _subscribed = true;
        }
    }

    private void OnDisable()
    {
        if (_subscribed && RelayManager.Instance != null)
        {
            RelayManager.Instance.OnStatus -= HandleStatus;
        }
        _subscribed = false;
    }

    private void HandleStatus(string msg)
    {
        _status = msg;
    }

    private void OnGUI()
    {
        PopUI.EnsureSkinFontApplied();
        GUIScaler.Begin();
        DrawContent();
        // 設定ウィンドウは、どの画面から開いても同じ見た目になるよう最上位の全画面オーバーレイとして描く
        if (_settingsOpen) DrawSettingsOverlay();
        GUIScaler.End();
    }

    private void DrawContent()
    {
        if (RelayManager.Instance == null)
        {
            GUILayout.BeginArea(new Rect(20, 20, 420, 60), PopUI.PanelStyle());
            GUILayout.Label("シーンにRelayManagerがありません。", PopUI.LabelStyle());
            GUILayout.EndArea();
            return;
        }

        bool connected = IsNetworkActive();
        var phase = MatchManager.Instance != null ? MatchManager.Instance.Phase : MatchManager.GamePhase.Lobby;

        if (connected && (phase == MatchManager.GamePhase.Playing || phase == MatchManager.GamePhase.Lobby
            || phase == MatchManager.GamePhase.Countdown))
        {
            DrawMinimalOverlay(phase);
            return;
        }

        // 未接続: タイトル画面
        if (!connected)
        {
            // 設定を開いている間はタイトル画面を描かない(ハンバーガーアイコンは常に描く)
            if (!_settingsOpen) DrawTitleScreen();
            DrawMenuIconAndDropdown();
            return;
        }

        // 勝敗確定後のGameOverだけ紙吹雪を出す
        if (phase == MatchManager.GamePhase.GameOver) DrawConfetti();

        var titleStyle = PopUI.TitleStyle(26, PopUI.Yellow);
        var statusStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true };
        statusStyle.normal.textColor = Color.white;

        const float areaWidth = 420f;
        GUILayout.BeginArea(new Rect(20, 20, areaWidth, GUIScaler.VirtualHeight - 40), PopUI.PanelStyle());

        GUILayout.Label("ボムロワイヤル", titleStyle);
        GUILayout.Label(_status, statusStyle);
        GUILayout.Space(12);

        // ここに来るのはRoundEnd/GameOverのみ
        DrawResultScreen(phase);

        GUILayout.EndArea();
    }

    private const string NicknameControlName = "TitleNicknameField";
    private bool _nicknameFieldWasFocused;

    /// <summary>タイトル画面。左寄せの角丸カード内にロゴとメニューを配置し、下端にヒントを出す</summary>
    private void DrawTitleScreen()
    {
        float contentX = GUIScaler.SafeAreaLeft + 56f;
        float contentWidth = Mathf.Min(440f, GUIScaler.VirtualWidth - contentX - GUIScaler.SafeAreaRight - 32f);
        float cardTop = 64f + GUIScaler.SafeAreaTop;

        const float cardPadding = 22f;
        const float logoBlockHeight = 162f; // ロゴブロックの高さ(タグライン下端まで)
        const float logoMenuGap = 28f;
        float menuContentHeight = EstimateMenuContentHeight();

        var cardRect = new Rect(
            contentX - cardPadding,
            cardTop - cardPadding,
            contentWidth + cardPadding * 2f,
            cardPadding * 2f + logoBlockHeight + logoMenuGap + menuContentHeight);
        DrawTitleCardBacking(cardRect);

        float logoBottom = DrawTitleLogoBlock(contentX, cardTop, contentWidth);

        GUILayout.BeginArea(new Rect(contentX, logoBottom + logoMenuGap, contentWidth, menuContentHeight + 8f));
        DrawTitleMenuContent();
        GUILayout.EndArea();

        DrawTitleFooter(contentX, contentWidth);
    }

    /// <summary>タイトル画面のUIカード背景(PanelStyleの9-sliceで角丸が潰れないようにする)</summary>
    private static void DrawTitleCardBacking(Rect rect)
    {
        GUI.Box(rect, GUIContent.none, PopUI.PanelStyle());
    }

    /// <summary>DrawTitleMenuContent()の描画高さを事前に見積もる(カード背景を先に描くため)</summary>
    private float EstimateMenuContentHeight()
    {
        if (RelayManager.Instance == null || !RelayManager.Instance.IsReady)
        {
            // 失敗時は「再試行」ボタンぶんの高さも加算する
            const float loadingLabelHeight = 24f;
            if (RelayManager.Instance != null && RelayManager.Instance.HasFailedToInitialize)
            {
                return loadingLabelHeight + 6f + 44f;
            }
            return loadingLabelHeight;
        }

        const float nameChipHeight = 32f;
        const float playHeight = 64f;
        const float lobbyToggleHeight = 44f;
        float height = nameChipHeight + 10f + playHeight + 16f + lobbyToggleHeight;
        if (_showJoinByCode) height += 6f + 40f; // 展開時のロビーコード入力行ぶん
        return height;
    }

    /// <summary>タイトルロゴ+サブタイトル+タグライン。戻り値はブロック下端のY座標</summary>
    private float DrawTitleLogoBlock(float x, float y, float width)
    {
        var logoStyle = new GUIStyle(GUI.skin.label) { fontSize = 56, fontStyle = FontStyle.Bold };
        var logoRect = new Rect(x, y, width, 72f);

        // 影と8方向の縁取りを重ねて、疑似太字の輪郭をはっきりさせる
        var shadowStyle = new GUIStyle(logoStyle);
        shadowStyle.normal.textColor = new Color(0f, 0f, 0f, 0.55f);
        GUI.Label(new Rect(logoRect.x + 4f, logoRect.y + 4f, logoRect.width, logoRect.height), "ボムロワイヤル", shadowStyle);
        logoStyle.normal.textColor = PopUI.Yellow;
        PopUI.DrawOutlinedLabel(logoRect, "ボムロワイヤル", logoStyle, new Color(0.05f, 0.05f, 0.07f, 0.9f), 2f);

        float subY = logoRect.yMax + 6f;

        var subStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold };
        subStyle.normal.textColor = new Color(1f, 1f, 1f, 0.75f);
        var subRect = new Rect(x, subY, width, 24f);
        GUI.Label(subRect, Localization.Get("ui.titleSubtitle"), subStyle);

        Color prevColor = GUI.color;
        GUI.color = PopUI.Yellow;
        GUI.DrawTexture(new Rect(x, subY + 26f, 64f, 3f), PopUI.SolidTexture);
        GUI.color = prevColor;

        float taglineY = subY + 40f;
        var taglineStyle = new GUIStyle(GUI.skin.label) { fontSize = 15, wordWrap = true };
        taglineStyle.normal.textColor = new Color(0.88f, 0.88f, 0.92f);
        var taglineRect = new Rect(x, taglineY, width, 44f);
        GUI.Label(taglineRect, Localization.Get("ui.titleTagline"), taglineStyle);

        return taglineRect.yMax;
    }

    /// <summary>プレイボタン・ニックネームチップ・ロビーコード参加をまとめたメニュー。初期化前/失敗時は読み込み中/再試行のみ出す</summary>
    private void DrawTitleMenuContent()
    {
        if (RelayManager.Instance == null || !RelayManager.Instance.IsReady)
        {
            // 失敗時はHandleStatus経由の理由(_status)を表示する
            bool failed = RelayManager.Instance != null && RelayManager.Instance.HasFailedToInitialize;
            GUILayout.Label(failed ? _status : Localization.Get("ui.loading"), PopUI.LabelStyle());

            if (failed && PopUI.Button(Localization.Get("ui.retry"), PopUI.ButtonStyle(), GUILayout.Height(44), GUILayout.Width(160)))
            {
                RelayManager.Instance.RetryInitialize();
            }
            return;
        }

        DrawNameChip();
        GUILayout.Space(10);

        // 唯一の塗りつぶしボタン。マッチング中は二重にPlayNow()が走らないよう無効化し、右にスピナーを重ねる
        bool isMatching = RelayManager.Instance.IsMatching;
        GUI.enabled = !isMatching;
        if (PopUI.Button("▶  " + Localization.Get("ui.play"), PopUI.PrimaryButtonStyle(24), GUILayout.Height(64), GUILayout.MaxWidth(480f)))
        {
            RelayManager.Instance.PlayNow();
        }
        GUI.enabled = true;
        if (isMatching)
        {
            Rect playButtonRect = GUILayoutUtility.GetLastRect();
            Vector2 spinnerCenter = new Vector2(playButtonRect.xMax - 30f, playButtonRect.center.y);
            DrawSpinner(spinnerCenter, 10f, 5f, new Color(0.08f, 0.08f, 0.1f));
        }

        GUILayout.Space(16);

        // 展開状態は▸/▾で示す
        string joinArrow = _showJoinByCode ? "▾ " : "▸ ";
        _showJoinByCode = PopUI.Toggle(_showJoinByCode, joinArrow + Localization.Get("ui.joinByCode"), PopUI.ChipButtonStyle(14), GUILayout.Height(44));

        if (_showJoinByCode)
        {
            GUILayout.BeginHorizontal();
            _lobbyCodeInput = GUILayout.TextField(_lobbyCodeInput, GUILayout.Height(40), GUILayout.Width(140));
            if (PopUI.Button(Localization.Get("ui.join"), PopUI.ButtonStyle(13), GUILayout.Width(80), GUILayout.Height(40)))
            {
                RelayManager.Instance.JoinGameByCode(_lobbyCodeInput);
            }
            GUILayout.EndHorizontal();
        }
    }

    private bool _nicknameEditorOpen;

    /// <summary>現在のニックネームを小さなチップで表示し、タップで入力欄を展開する</summary>
    private void DrawNameChip()
    {
        if (_nameInput == null) _nameInput = DisplayNameUI.GetSavedName();

        if (!_nicknameEditorOpen)
        {
            if (PopUI.Button(Localization.Format("ui.nicknameChip", _nameInput), PopUI.ChipButtonStyle(14), GUILayout.Height(32)))
            {
                _nicknameEditorOpen = true;
            }
            return;
        }

        GUILayout.BeginHorizontal();
        GUI.SetNextControlName(NicknameControlName);
        string newValue = GUILayout.TextField(_nameInput, 12, GUILayout.Height(32), GUILayout.Width(160));
        if (newValue != _nameInput)
        {
            _nameInput = newValue;
            DisplayNameUI.SetSavedName(_nameInput);
        }

        if (PopUI.Button(Localization.Get("ui.close"), PopUI.ChipButtonStyle(13), GUILayout.Height(32), GUILayout.Width(70)))
        {
            _nicknameEditorOpen = false;
        }
        GUILayout.EndHorizontal();

        // 空のまま閉じたら、表示も保存済みの名前へ戻す(SetSavedNameは空を保存しないため)
        bool isFocused = GUI.GetNameOfFocusedControl() == NicknameControlName;
        if ((_nicknameFieldWasFocused && !isFocused || !_nicknameEditorOpen) && string.IsNullOrWhiteSpace(_nameInput))
        {
            _nameInput = DisplayNameUI.GetSavedName();
        }
        _nicknameFieldWasFocused = isFocused;
    }

    /// <summary>画面下端に、数秒おきに切り替わるヒントテキストを表示する</summary>
    private void DrawTitleFooter(float x, float width)
    {
        if (Time.time >= _tipChangeTime)
        {
            _tipIndex = (_tipIndex + 1) % TitleTipKeys.Length;
            _tipChangeTime = Time.time + 4.5f;
            _tipFadeStartTime = Time.time;
        }

        // 切り替え時は0.3秒のアルファフェードのみ
        const float fadeDuration = 0.3f;
        float fadeAlpha = fadeDuration > 0f ? Mathf.Clamp01((Time.time - _tipFadeStartTime) / fadeDuration) : 1f;

        // 可読性のためヒント行に小さな角丸パネルを敷く(SafeAreaBottomも考慮)
        const float blockHeight = 74f;
        const float blockPadding = 10f;
        float blockY = GUIScaler.VirtualHeight - GUIScaler.SafeAreaBottom - blockHeight;
        var backingRect = new Rect(x - blockPadding, blockY - blockPadding, width + blockPadding * 2f, blockHeight + blockPadding);
        GUI.DrawTexture(backingRect, PopUI.PanelTexture);

        var tipsLabelStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold };
        tipsLabelStyle.normal.textColor = new Color(PopUI.Yellow.r, PopUI.Yellow.g, PopUI.Yellow.b, 0.9f * fadeAlpha);
        GUI.Label(new Rect(x, blockY, 60f, 18f), Localization.Get("ui.tipsLabel"), tipsLabelStyle);

        var tipStyle = new GUIStyle(GUI.skin.label) { fontSize = 15, wordWrap = true };
        tipStyle.normal.textColor = new Color(1f, 1f, 1f, 0.8f * fadeAlpha);
        GUI.Label(new Rect(x, blockY + 20f, width, 30f), Localization.Get(TitleTipKeys[_tipIndex]), tipStyle);
    }

    /// <summary>画面全体に紙吹雪を降らせる背景演出(単色矩形を回転させて描く)</summary>
    private void DrawConfetti()
    {
        if (_confetti == null)
        {
            _confetti = new ConfettiPiece[26];
            for (int i = 0; i < _confetti.Length; i++)
            {
                _confetti[i] = NewConfettiPiece(Random.Range(0f, GUIScaler.VirtualHeight));
            }
        }

        Matrix4x4 savedMatrix = GUI.matrix;
        Color savedColor = GUI.color;

        for (int i = 0; i < _confetti.Length; i++)
        {
            ConfettiPiece piece = _confetti[i];
            piece.Position.y += piece.FallSpeed * Time.deltaTime;
            piece.Rotation += piece.RotationSpeed * Time.deltaTime;
            if (piece.Position.y > GUIScaler.VirtualHeight + 20f)
            {
                piece = NewConfettiPiece(-20f);
            }
            _confetti[i] = piece;

            // RotateAroundPivotの基準点には、掛かっているスケールぶんGUIScaler.Scaleを掛ける
            GUIUtility.RotateAroundPivot(piece.Rotation, piece.Position * GUIScaler.Scale);
            GUI.color = piece.Color;
            GUI.DrawTexture(new Rect(piece.Position.x, piece.Position.y, piece.Width, piece.Height), PopUI.SolidTexture);
            GUI.matrix = savedMatrix;
        }

        GUI.color = savedColor;
    }

    private ConfettiPiece NewConfettiPiece(float startY)
    {
        return new ConfettiPiece
        {
            Position = new Vector2(Random.Range(0f, GUIScaler.VirtualWidth), startY),
            FallSpeed = Random.Range(35f, 75f),
            RotationSpeed = Random.Range(-160f, 160f),
            Rotation = Random.Range(0f, 360f),
            Color = PopUI.AccentFor(Random.Range(0, 6)),
            Width = Random.Range(6f, 11f),
            Height = Random.Range(10f, 16f),
        };
    }

    /// <summary>プレイボタンのマッチング中に重ねる、円状に尾を引くドットスピナー</summary>
    private static void DrawSpinner(Vector2 center, float radius, float dotSize, Color color)
    {
        const int dotCount = 8;
        float headAngle = Time.time * 360f; // 1秒に1周
        Color savedColor = GUI.color;

        for (int i = 0; i < dotCount; i++)
        {
            float angle = (headAngle - i * (360f / dotCount)) * Mathf.Deg2Rad;
            Vector2 pos = center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
            float tailFade = 1f - (i / (float)dotCount); // 先頭が最も濃く、末尾ほど薄く小さくなる「尾」
            float size = dotSize * Mathf.Lerp(0.35f, 1f, tailFade);
            GUI.color = new Color(color.r, color.g, color.b, color.a * tailFade);
            GUI.DrawTexture(new Rect(pos.x - size * 0.5f, pos.y - size * 0.5f, size, size), PopUI.SolidTexture);
        }

        GUI.color = savedColor;
    }

    /// <summary>RoundEnd/GameOverの結果画面(この2フェーズだけフル表示のまま)</summary>
    private void DrawResultScreen(MatchManager.GamePhase phase)
    {
        bool isHost = NetworkManager.Singleton != null && NetworkManager.Singleton.IsHost;
        var buttonStyle = PopUI.ButtonStyle();

        var winnerStyle = PopUI.TitleStyle(20, PopUI.Cyan);
        GUILayout.Label($"フェーズ: {phase}", PopUI.LabelStyle());

        ulong winnerId = MatchManager.Instance != null ? MatchManager.Instance.WinnerClientId.Value : ulong.MaxValue;
        GUILayout.Label(winnerId == ulong.MaxValue ? "引き分け" : $"勝者: {PlayerController.ResolveDisplayName(winnerId)}", winnerStyle);

        GUILayout.Space(8);
        DrawKillRanking();

        if (phase == MatchManager.GamePhase.GameOver && isHost)
        {
            if (PopUI.Button(Localization.Get("gameover.returnLobby"), buttonStyle, GUILayout.Height(40)))
            {
                MatchManager.Instance.ReturnToLobby();
            }
        }

        GUILayout.Space(16);
        if (PopUI.Button(Localization.Get("ui.disconnect"), buttonStyle, GUILayout.Height(36)))
        {
            RelayManager.Instance.LeaveGame();
        }
    }

    /// <summary>キル数の多い順に並べたランキング</summary>
    private void DrawKillRanking()
    {
        var ranked = new List<PlayerController>(PlayerController.ActiveInstances);
        ranked.Sort((a, b) => b.KillCount.Value.CompareTo(a.KillCount.Value));

        if (ranked.Count == 0) return;

        var headerStyle = PopUI.LabelStyle(13);
        headerStyle.fontStyle = FontStyle.Bold;
        GUILayout.Label("キル数ランキング", headerStyle);

        var rowStyle = PopUI.LabelStyle(13);
        for (int i = 0; i < ranked.Count; i++)
        {
            PlayerController pc = ranked[i];
            if (pc == null) continue;

            Color rowColor = i == 0 ? PopUI.Yellow : Color.white;
            var style = new GUIStyle(rowStyle) { normal = { textColor = rowColor } };
            GUILayout.Label($"{i + 1}位  {pc.DisplayName}  -  {pc.KillCount.Value} kills  /  {Mathf.RoundToInt(pc.TotalDamageDealt.Value)} dmg", style);
        }
    }

    private static Texture2D _menuIconTexture;

    /// <summary>接続済みのLobby/Playing中に出す右上の小さいオーバーレイ(Lobbyではプレイヤー数と開始ボタン、≡メニュー)</summary>
    private void DrawMinimalOverlay(MatchManager.GamePhase phase)
    {
        bool isLobby = phase == MatchManager.GamePhase.Lobby;

        if (isLobby)
        {
            bool isHost = NetworkManager.Singleton != null && NetworkManager.Singleton.IsHost;

            // 開始ボタンはタイトルのプレイボタンと同格の主役スタイルにする
            const float infoWidth = 240f;
            const float startButtonHeight = 56f;
            float infoHeight = isHost ? 112f : 84f;
            // アイコンのさらに左に並べるため、SafeAreaを考慮して配置する
            GUILayout.BeginArea(new Rect(
                GUIScaler.VirtualWidth - GUIScaler.SafeAreaRight - infoWidth - 56,
                GUIScaler.SafeAreaTop + 12,
                infoWidth, infoHeight), PopUI.PanelStyle());

            // メニュー/設定を開いている間は、重なって描かれた裏のボタンがクリックを横取りしないよう入力を止める
            bool prevGuiEnabled = GUI.enabled;
            if (IsUnderlyingInputBlocked()) GUI.enabled = false;

            int connectedCount = NetworkManager.Singleton != null ? NetworkManager.Singleton.ConnectedClientsList.Count : 0;
            var connectedStyle = new GUIStyle(GUI.skin.label) { fontSize = 13 };
            connectedStyle.normal.textColor = Color.white;
            GUILayout.Label(Localization.Format("lobby.connected", connectedCount), connectedStyle);

            if (isHost)
            {
                GUILayout.Space(6);
                if (PopUI.Button("▶  " + Localization.Get("lobby.start"), PopUI.PrimaryButtonStyle(18), GUILayout.Height(startButtonHeight)))
                {
                    MatchManager.Instance.StartMatch();
                }
            }
            else
            {
                GUILayout.Label(Localization.Get("lobby.waitingHost"), PopUI.LabelStyle(13));
            }

            GUI.enabled = prevGuiEnabled;
            GUILayout.EndArea();
        }

        if (phase == MatchManager.GamePhase.Countdown) DrawCountdownBanner();
        if (phase == MatchManager.GamePhase.Playing || phase == MatchManager.GamePhase.Countdown) DrawMatchStatusHud();

        DrawMenuIconAndDropdown();
    }

    /// <summary>試合中、画面上部中央に生存者数・残り時間・自分のキル数を表示する</summary>
    private void DrawMatchStatusHud()
    {
        if (MatchManager.Instance == null) return;

        int alive = MatchManager.Instance.AliveCount.Value;
        float remaining = MatchManager.Instance.TimeRemaining.Value;
        int myKills = PlayerController.LocalPlayer != null ? PlayerController.LocalPlayer.KillCount.Value : 0;

        string timeText = remaining > 0f
            ? $"{Mathf.FloorToInt(remaining / 60f)}:{Mathf.FloorToInt(remaining % 60f):00}"
            : "--:--";

        const float chipHeight = 56f;
        const float aliveChipWidth = 108f;
        const float timeChipWidth = 124f;
        const float killsChipWidth = 92f;
        const float gap = 8f;
        float totalWidth = aliveChipWidth + gap + timeChipWidth + gap + killsChipWidth;

        float x = GUIScaler.VirtualWidth / 2f - totalWidth / 2f;
        float y = GUIScaler.SafeAreaTop + 12f;

        x = DrawStatusChip(x, y, aliveChipWidth, chipHeight, BuildPersonIconTexture(48),
            new Color(0.5f, 0.85f, 1f), alive.ToString(), 28);
        x += gap;
        x = DrawStatusChip(x, y, timeChipWidth, chipHeight, BuildClockIconTexture(48),
            remaining > 0f && remaining <= 30f ? new Color(1f, 0.35f, 0.35f) : PopUI.Yellow, timeText, 28);
        x += gap;

        // killsは優先度が低いので、アイコン無し+薄い文字色にする
        var killsRect = new Rect(x, y, killsChipWidth, chipHeight);
        GUI.DrawTexture(killsRect, PopUI.PanelTexture);
        var killsStyle = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        killsStyle.normal.textColor = new Color(1f, 1f, 1f, 0.75f);
        GUI.Label(killsRect, $"{myKills} kills", killsStyle);
    }

    /// <summary>生存者数/残り時間チップの描画(アイコン+数値)。戻り値はチップの右端X座標</summary>
    private static float DrawStatusChip(float x, float y, float width, float height, Texture2D icon,
        Color accentColor, string valueText, int valueFontSize)
    {
        var rect = new Rect(x, y, width, height);
        GUI.DrawTexture(rect, PopUI.PanelTexture);

        const float iconSize = 30f;
        const float padding = 10f;
        var iconRect = new Rect(rect.x + padding, rect.y + (height - iconSize) / 2f, iconSize, iconSize);
        Color prevColor = GUI.color;
        GUI.color = accentColor;
        GUI.DrawTexture(iconRect, icon);
        GUI.color = prevColor;

        var valueRect = new Rect(iconRect.xMax + 4f, rect.y, width - iconSize - padding - 4f, height);
        var valueStyle = new GUIStyle(GUI.skin.label) { fontSize = valueFontSize, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        valueStyle.normal.textColor = Color.white;
        GUI.Label(valueRect, valueText, valueStyle);

        return rect.xMax;
    }

    private static Texture2D _personIconTexture;
    private static Texture2D _clockIconTexture;

    /// <summary>簡易的な人型シルエットのアイコンをコード生成する</summary>
    private static Texture2D BuildPersonIconTexture(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var clear = new Color(1f, 1f, 1f, 0f);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                tex.SetPixel(x, y, clear);

        Vector2 headCenter = new Vector2(size / 2f, size * 0.72f);
        float headRadius = size * 0.18f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), headCenter) <= headRadius)
                {
                    tex.SetPixel(x, y, Color.white);
                }
            }
        }

        int bodyTop = Mathf.RoundToInt(size * 0.5f);
        int bodyBottom = Mathf.RoundToInt(size * 0.08f);
        float halfWidthAtBottom = size * 0.34f;
        for (int y = bodyBottom; y < bodyTop; y++)
        {
            float t = (float)(y - bodyBottom) / Mathf.Max(1, bodyTop - bodyBottom);
            int halfWidth = Mathf.RoundToInt(Mathf.Lerp(halfWidthAtBottom, headRadius * 0.9f, t));
            int cx = size / 2;
            for (int x = cx - halfWidth; x < cx + halfWidth; x++)
            {
                if (x < 0 || x >= size) continue;
                tex.SetPixel(x, y, Color.white);
            }
        }

        tex.Apply();
        return tex;
    }

    /// <summary>簡易的な時計アイコンをコード生成する</summary>
    private static Texture2D BuildClockIconTexture(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var clear = new Color(1f, 1f, 1f, 0f);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                tex.SetPixel(x, y, clear);

        Vector2 center = new Vector2(size / 2f, size / 2f);
        float outerRadius = size * 0.46f;
        float innerRadius = outerRadius - Mathf.Max(2f, size * 0.08f);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), center);
                if (d <= outerRadius && d >= innerRadius)
                {
                    tex.SetPixel(x, y, Color.white);
                }
            }
        }

        // 短針(上向き)と長針(右向き)
        DrawThickLine(tex, center, center + new Vector2(0f, outerRadius * 0.55f), Mathf.Max(1f, size * 0.07f));
        DrawThickLine(tex, center, center + new Vector2(outerRadius * 0.4f, 0f), Mathf.Max(1f, size * 0.07f));

        tex.Apply();
        return tex;
    }

    private static void DrawThickLine(Texture2D tex, Vector2 from, Vector2 to, float thickness)
    {
        int size = tex.width;
        Vector2 dir = (to - from);
        float length = dir.magnitude;
        if (length < 0.0001f) return;
        dir /= length;
        Vector2 normal = new Vector2(-dir.y, dir.x);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                Vector2 p = new Vector2(x + 0.5f, y + 0.5f) - from;
                float along = Vector2.Dot(p, dir);
                if (along < 0f || along > length) continue;
                float side = Mathf.Abs(Vector2.Dot(p, normal));
                if (side <= thickness * 0.5f) tex.SetPixel(x, y, Color.white);
            }
        }
    }

    /// <summary>試合開始前の「3, 2, 1, START!」カウントダウン表示</summary>
    private void DrawCountdownBanner()
    {
        if (MatchManager.Instance == null) return;

        float remaining = MatchManager.Instance.CountdownRemaining.Value;
        int displayNumber = Mathf.CeilToInt(remaining);
        string text = displayNumber > 0 ? displayNumber.ToString() : "START!";

        var style = new GUIStyle(GUI.skin.label)
        {
            fontSize = 72,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
        };
        style.normal.textColor = PopUI.Yellow;

        float cx = GUIScaler.VirtualWidth / 2f;
        float cy = GUIScaler.VirtualHeight * 0.35f;
        GUI.Label(new Rect(cx - 200f, cy - 60f, 400f, 120f), text, style);
    }

    private bool _settingsOpen;
    private bool _menuDropdownOpen;

    /// <summary>ハンバーガー/設定を開いている間、下の画面のボタンがクリックを横取りしないよう入力を止める判定(描画中はfalse)</summary>
    private bool IsUnderlyingInputBlocked()
    {
        if (!_menuDropdownOpen && !_settingsOpen) return false;
        return Event.current != null && Event.current.type != EventType.Repaint;
    }

    /// <summary>右上のハンバーガー(≡)アイコンと、押すと開く「設定」ドロップダウンを描画する</summary>
    private void DrawMenuIconAndDropdown()
    {
        if (_menuIconTexture == null) _menuIconTexture = BuildMenuIconTexture(34);

        const float iconSize = 48f;

        // 切り欠きの外に出ないよう、SafeAreaを考慮して配置する
        var menuIconRect = new Rect(
            GUIScaler.VirtualWidth - GUIScaler.SafeAreaRight - iconSize - 12,
            GUIScaler.SafeAreaTop + 12,
            iconSize, iconSize);
        GUI.DrawTexture(menuIconRect, PopUI.PanelTexture);
        GUI.DrawTexture(new Rect(menuIconRect.x + 7, menuIconRect.y + 7, iconSize - 14, iconSize - 14), _menuIconTexture);
        if (PopUI.RectButton(menuIconRect, GUIContent.none, GUIStyle.none))
        {
            _menuDropdownOpen = !_menuDropdownOpen;
        }

        if (!_menuDropdownOpen) return;

        const float dropdownWidth = 150f;
        const float dropdownHeight = 44f;
        var dropdownRect = new Rect(menuIconRect.xMax - dropdownWidth, menuIconRect.yMax + 6f, dropdownWidth, dropdownHeight);
        GUI.Box(dropdownRect, GUIContent.none, PopUI.PanelStyle());
        if (PopUI.RectButton(dropdownRect, Localization.Get("ui.settingsTitle"), PopUI.ChipButtonStyle(16)))
        {
            _settingsOpen = true;
            _menuDropdownOpen = false;
        }
    }

    private enum SettingsCategory { Display, Graphics, Sound, Controls, Language }
    private SettingsCategory _activeSettingsCategory = SettingsCategory.Display;

    /// <summary>全画面の半透明ディム+中央パネルの設定ウィンドウ(左にカテゴリ、右に中身、下に終了/切断/閉じる)。幅はCalcSizeで実測する</summary>
    private void DrawSettingsOverlay()
    {
        Color prevColor = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.65f);
        GUI.DrawTexture(new Rect(0, 0, GUIScaler.VirtualWidth, GUIScaler.VirtualHeight), PopUI.SolidTexture);
        GUI.color = prevColor;

        const float buttonHeight = 44f; // Androidのタップ領域目安(44dp相当)
        const float categoryButtonHeight = 40f;
        const float gap = 12f;
        const float sectionGap = gap * 2f; // 区切り用は行間より広く取る
        // BeginArea(rect, style)はstyleのpaddingを暗黙に差し引くため、余白はcontentInsetで明示的に管理する
        const float contentInset = 24f;
        const float columnGap = 18f;
        const float leftColumnWidth = 110f;
        const float swatchReserve = 32f; // 色ボタンの右端に重ねる色見本スウォッチぶんの余白
        const float minRightWidth = 220f;

        var titleStyle = PopUI.TitleStyle(20, PopUI.Yellow);
        var buttonStyle = PopUI.ButtonStyle(18);
        var categoryStyle = PopUI.ChipButtonStyle(15);

        bool showQuit = !Application.isMobilePlatform;
        // 接続中(トランスポート起動済み)の間だけ「切断」を出す
        bool showDisconnect = NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;

        string titleText = Localization.Get("ui.settingsTitle");
        string displayCategoryLabel = Localization.Get("ui.settingsCategoryDisplay");
        string controlsCategoryLabel = Localization.Get("ui.settingsCategoryControls");
        string languageCategoryLabel = Localization.Get("ui.settingsCategoryLanguage");
        string graphicsCategoryLabel = Localization.Get("ui.settingsCategoryGraphics");
        string soundCategoryLabel = Localization.Get("ui.settingsCategorySound");
        string graphicsQualityLabel = Localization.Format("ui.graphicsQualityLabel",
            Localization.Get(GraphicsQualitySettings.LevelLocalizationKey));
        string graphicsHint = Localization.Get(GraphicsQualitySettings.LevelHintLocalizationKey);
        string fpsLabel = Localization.Get(GraphicsQualitySettings.ShowFps ? "ui.fpsOn" : "ui.fpsOff");
        string outlineLabel = Localization.Get(OutlineSettings.Enabled ? "ui.outlineOn" : "ui.outlineOff");
        string colorLabel = Localization.Get("ui.outlineColor");
        string damageDisplayLabel = Localization.Get(DamageDisplaySettings.Enabled ? "ui.damageDisplayOn" : "ui.damageDisplayOff");
        // スライダー操作中も毎フレーム変わるため、都度最新値を文言へ差し込む
        string sensitivityLabel = Localization.Format("ui.sensitivityLabel", LookSensitivitySettings.Multiplier);
        // 現在の言語名をボタンラベルに出す
        string languageLabel = Localization.Format("ui.languageLabel",
            Localization.Current == Language.Japanese ? "日本語" : "English");
        string closeLabel = Localization.Get("ui.close");
        string quitLabel = Localization.Get("ui.quit");
        string disconnectLabel = Localization.Get("ui.disconnect");

        float rightContentWidth = Mathf.Max(minRightWidth, Mathf.Max(
            buttonStyle.CalcSize(new GUIContent(outlineLabel)).x,
            Mathf.Max(
                buttonStyle.CalcSize(new GUIContent(colorLabel)).x + swatchReserve,
                Mathf.Max(
                    buttonStyle.CalcSize(new GUIContent(languageLabel)).x,
                    Mathf.Max(
                        buttonStyle.CalcSize(new GUIContent(sensitivityLabel)).x,
                        Mathf.Max(
                            buttonStyle.CalcSize(new GUIContent(graphicsQualityLabel)).x,
                            Mathf.Max(
                                buttonStyle.CalcSize(new GUIContent(fpsLabel)).x,
                                buttonStyle.CalcSize(new GUIContent(damageDisplayLabel)).x)))))));

        // 終了/切断/閉じるが横並び1行になったため、必要幅は表示するボタン分の合計(+間のgap)で見積もる
        // サウンドカテゴリは「ラベル(固定幅)+スライダー」の横並びなので、スライダーが細くなりすぎない幅を確保する
        const float volumeLabelWidth = 140f;
        const float volumeRowMinWidth = volumeLabelWidth + 170f;
        rightContentWidth = Mathf.Max(rightContentWidth, volumeRowMinWidth);

        float bottomRowWidth = buttonStyle.CalcSize(new GUIContent(closeLabel)).x;
        if (showQuit) bottomRowWidth += gap + buttonStyle.CalcSize(new GUIContent(quitLabel)).x;
        if (showDisconnect) bottomRowWidth += gap + buttonStyle.CalcSize(new GUIContent(disconnectLabel)).x;

        float mainRowWidth = leftColumnWidth + columnGap + rightContentWidth;
        float contentWidth = Mathf.Max(mainRowWidth, Mathf.Max(bottomRowWidth, titleStyle.CalcSize(new GUIContent(titleText)).x));
        float panelWidth = contentWidth + contentInset * 2f;

        const float headerHeight = 34f;

        // 中央2カラム部分の高さは左右の高い方に揃える(カテゴリを切り替えてもパネルサイズが変わらないようにする)
        float leftColumnHeight = categoryButtonHeight * 5f + gap * 4f;
        float rightColumnHeight = headerHeight + gap + buttonHeight * 3f + gap * 2f;
        float middleHeight = Mathf.Max(leftColumnHeight, rightColumnHeight);

        // 各要素のmarginで手計算の高さが数px不足し得るため、安全マージンを加える
        const float heightSafetyBuffer = 28f;
        float titleHeight = titleStyle.CalcSize(new GUIContent(titleText)).y;
        float panelHeight = contentInset * 2f
            + titleHeight + sectionGap
            + middleHeight
            + sectionGap + buttonHeight // 終了+閉じる(横並び1行)
            + heightSafetyBuffer;

        var panelRect = new Rect(
            (GUIScaler.VirtualWidth - panelWidth) / 2f,
            (GUIScaler.VirtualHeight - panelHeight) / 2f,
            panelWidth, panelHeight);

        GUI.Box(panelRect, GUIContent.none, PopUI.PanelStyle());
        var contentRect = new Rect(
            panelRect.x + contentInset, panelRect.y + contentInset,
            panelRect.width - contentInset * 2f, panelRect.height - contentInset * 2f);
        GUILayout.BeginArea(contentRect);

        GUILayout.Label(titleText, titleStyle);
        GUILayout.Space(sectionGap);

        GUILayout.BeginHorizontal();

        // 左: カテゴリ一覧(縦のサイドバー)
        GUILayout.BeginVertical(GUILayout.Width(leftColumnWidth), GUILayout.Height(middleHeight));
        if (PopUI.Toggle(_activeSettingsCategory == SettingsCategory.Display, displayCategoryLabel, categoryStyle, GUILayout.Height(categoryButtonHeight)))
        {
            _activeSettingsCategory = SettingsCategory.Display;
        }
        GUILayout.Space(gap);
        if (PopUI.Toggle(_activeSettingsCategory == SettingsCategory.Graphics, graphicsCategoryLabel, categoryStyle, GUILayout.Height(categoryButtonHeight)))
        {
            _activeSettingsCategory = SettingsCategory.Graphics;
        }
        GUILayout.Space(gap);
        if (PopUI.Toggle(_activeSettingsCategory == SettingsCategory.Sound, soundCategoryLabel, categoryStyle, GUILayout.Height(categoryButtonHeight)))
        {
            _activeSettingsCategory = SettingsCategory.Sound;
        }
        GUILayout.Space(gap);
        if (PopUI.Toggle(_activeSettingsCategory == SettingsCategory.Controls, controlsCategoryLabel, categoryStyle, GUILayout.Height(categoryButtonHeight)))
        {
            _activeSettingsCategory = SettingsCategory.Controls;
        }
        GUILayout.Space(gap);
        if (PopUI.Toggle(_activeSettingsCategory == SettingsCategory.Language, languageCategoryLabel, categoryStyle, GUILayout.Height(categoryButtonHeight)))
        {
            _activeSettingsCategory = SettingsCategory.Language;
        }
        GUILayout.EndVertical();

        GUILayout.Space(columnGap);

        // 右: 選択中カテゴリの中身(先頭に見出しバー)
        GUILayout.BeginVertical(GUILayout.Width(rightContentWidth), GUILayout.Height(middleHeight));
        if (_activeSettingsCategory == SettingsCategory.Display)
        {
            DrawSettingsSectionHeader(Localization.Get("ui.settingsHeaderDisplay"));
            GUILayout.Space(gap);

            if (PopUI.Button(outlineLabel, buttonStyle, GUILayout.Height(buttonHeight)))
            {
                OutlineSettings.Enabled = !OutlineSettings.Enabled;
            }

            GUILayout.Space(gap);

            // 色プリセットの見本を、切り替えボタンの右側に現在の色として小さく表示する
            GUILayout.BeginHorizontal();
            if (PopUI.Button(colorLabel, buttonStyle, GUILayout.Height(buttonHeight)))
            {
                OutlineSettings.CycleColor();
            }
            Rect swatchRect = GUILayoutUtility.GetLastRect();
            var swatchColorRect = new Rect(swatchRect.xMax - 32f, swatchRect.y + 12f, 20f, 20f);
            Color prevSwatchColor = GUI.color;
            GUI.color = OutlineSettings.CurrentColor;
            GUI.DrawTexture(swatchColorRect, PopUI.SolidTexture);
            GUI.color = prevSwatchColor;
            GUILayout.EndHorizontal();

            GUILayout.Space(gap);

            // ヒットマーカーとダメージ数字の表示をまとめて切り替える
            if (PopUI.Button(damageDisplayLabel, buttonStyle, GUILayout.Height(buttonHeight)))
            {
                DamageDisplaySettings.Enabled = !DamageDisplaySettings.Enabled;
            }
        }
        else if (_activeSettingsCategory == SettingsCategory.Graphics)
        {
            DrawSettingsSectionHeader(Localization.Get("ui.settingsHeaderGraphics"));
            GUILayout.Space(gap);

            // 低→中→高の巡回
            if (PopUI.Button(graphicsQualityLabel, buttonStyle, GUILayout.Height(buttonHeight)))
            {
                GraphicsQualitySettings.CycleLevel();
            }

            GUILayout.Space(gap);

            if (PopUI.Button(fpsLabel, buttonStyle, GUILayout.Height(buttonHeight)))
            {
                GraphicsQualitySettings.ShowFps = !GraphicsQualitySettings.ShowFps;
            }

            // 現在のプリセットで何が変わるかの説明
            GUILayout.Space(gap);
            GUILayout.Label(graphicsHint, PopUI.LabelStyle(12, true), GUILayout.Height(36f));
        }
        else if (_activeSettingsCategory == SettingsCategory.Sound)
        {
            // 全体/BGM/効果音の3本。保存と反映はSoundManager側に任せる
            DrawSettingsSectionHeader(Localization.Get("ui.settingsHeaderSound"));
            GUILayout.Space(gap);

            SoundManager soundManager = SoundManager.Instance;
            if (soundManager == null)
            {
                GUILayout.Label("SoundManager not found", PopUI.LabelStyle(13));
            }
            else
            {
                float master = DrawVolumeRow("ui.volumeMaster", soundManager.MasterVolume, volumeLabelWidth, buttonHeight);
                if (!Mathf.Approximately(master, soundManager.MasterVolume)) soundManager.SetMasterVolume(master);
                GUILayout.Space(gap);

                float bgm = DrawVolumeRow("ui.volumeBgm", soundManager.BgmVolume, volumeLabelWidth, buttonHeight);
                if (!Mathf.Approximately(bgm, soundManager.BgmVolume)) soundManager.SetBgmVolume(bgm);
                GUILayout.Space(gap);

                float sfx = DrawVolumeRow("ui.volumeSfx", soundManager.SfxVolume, volumeLabelWidth, buttonHeight);
                if (!Mathf.Approximately(sfx, soundManager.SfxVolume)) soundManager.SetSfxVolume(sfx);
            }
        }
        else if (_activeSettingsCategory == SettingsCategory.Controls)
        {
            // 見出し+ラベル+スライダーの高さ予算に収まるよう、各行をbuttonHeightに揃える
            DrawSettingsSectionHeader(Localization.Get("ui.settingsHeaderControls"));
            GUILayout.Space(gap);

            GUILayout.Label(sensitivityLabel, buttonStyle, GUILayout.Height(buttonHeight));
            LookSensitivitySettings.Multiplier = GUILayout.HorizontalSlider(
                LookSensitivitySettings.Multiplier, LookSensitivitySettings.Min, LookSensitivitySettings.Max,
                GUILayout.Height(buttonHeight));
        }
        else
        {
            DrawSettingsSectionHeader(Localization.Get("ui.settingsHeaderLanguage"));
            GUILayout.Space(gap);

            if (PopUI.Button(languageLabel, buttonStyle, GUILayout.Height(buttonHeight)))
            {
                Localization.Current = Localization.Current == Language.Japanese ? Language.English : Language.Japanese;
            }
        }
        GUILayout.EndVertical();

        GUILayout.EndHorizontal();

        // 終了/切断/閉じるを下段1行に横並びで置く
        GUILayout.Space(sectionGap);
        GUILayout.BeginHorizontal();
        if (showQuit)
        {
            // モバイルではOS標準操作で終了するため「終了」ボタンは出さない。危険操作として赤みがかった色にする
            Color prevQuitColor = GUI.color;
            GUI.color = new Color(1f, 0.55f, 0.55f);
            if (PopUI.Button(quitLabel, buttonStyle, GUILayout.Height(buttonHeight)))
            {
                QuitApplication();
            }
            GUI.color = prevQuitColor;
            GUILayout.Space(gap);
        }
        if (showDisconnect)
        {
            // 終了と同じく危険操作の色にする
            Color prevDisconnectColor = GUI.color;
            GUI.color = new Color(1f, 0.55f, 0.55f);
            if (PopUI.Button(disconnectLabel, buttonStyle, GUILayout.Height(buttonHeight)))
            {
                _settingsOpen = false;
                RelayManager.Instance?.LeaveGame();
            }
            GUI.color = prevDisconnectColor;
            GUILayout.Space(gap);
        }
        if (PopUI.Button(closeLabel, buttonStyle, GUILayout.Height(buttonHeight)))
        {
            _settingsOpen = false;
        }
        GUILayout.EndHorizontal();

        GUILayout.EndArea();
    }

    /// <summary>アプリを終了する(Editorでは再生を停止する)</summary>
    private static void QuitApplication()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    /// <summary>サウンド設定の1行(「BGM: 60%」のラベル+スライダー)。スライダーの新しい値を返す</summary>
    private static float DrawVolumeRow(string labelKey, float value, float labelWidth, float rowHeight)
    {
        GUILayout.BeginHorizontal();
        var labelStyle = PopUI.LabelStyle(16);
        labelStyle.alignment = TextAnchor.MiddleLeft;
        GUILayout.Label(Localization.Format(labelKey, Mathf.RoundToInt(value * 100f)), labelStyle,
            GUILayout.Width(labelWidth), GUILayout.Height(rowHeight));
        float newValue = GUILayout.HorizontalSlider(value, 0f, 1f, GUILayout.Height(rowHeight), GUILayout.ExpandWidth(true));
        GUILayout.EndHorizontal();
        return newValue;
    }

    /// <summary>設定パネル右側の先頭に置く色付きの見出しバー</summary>
    private static void DrawSettingsSectionHeader(string label)
    {
        Rect rect = GUILayoutUtility.GetRect(0, 34f, GUILayout.ExpandWidth(true));
        GUI.Box(rect, GUIContent.none, PopUI.PanelStyle());

        Color prevColor = GUI.color;
        GUI.color = PopUI.Yellow;
        GUI.DrawTexture(new Rect(rect.x + 4f, rect.y + 5f, 4f, rect.height - 10f), PopUI.SolidTexture);
        GUI.color = prevColor;

        var headerStyle = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
        headerStyle.normal.textColor = PopUI.Yellow;
        GUI.Label(new Rect(rect.x + 14f, rect.y, rect.width - 18f, rect.height), label, headerStyle);
    }

    /// <summary>ハンバーガーメニュー(≡)アイコンを白の横棒3本として生成する</summary>
    private static Texture2D BuildMenuIconTexture(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var clear = new Color(1f, 1f, 1f, 0f);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                tex.SetPixel(x, y, clear);
            }
        }

        int barThickness = Mathf.Max(2, size / 8);
        int[] barCentersY = { size / 5, size / 2, size - size / 5 };
        foreach (int cy in barCentersY)
        {
            for (int y = cy - barThickness / 2; y < cy + barThickness / 2; y++)
            {
                if (y < 0 || y >= size) continue;
                for (int x = 0; x < size; x++)
                {
                    tex.SetPixel(x, y, Color.white);
                }
            }
        }

        tex.Apply();
        return tex;
    }

    private bool IsNetworkActive()
    {
        return NetworkManager.Singleton != null &&
               (NetworkManager.Singleton.IsHost || NetworkManager.Singleton.IsClient);
    }
}
